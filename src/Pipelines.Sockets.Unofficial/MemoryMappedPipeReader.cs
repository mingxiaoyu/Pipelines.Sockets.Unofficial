﻿using System;
using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.IO.Pipelines;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Pipelines.Sockets.Unofficial
{
    /// <summary>
    /// Represents a pipe that iterates over a memory-mapped file
    /// </summary>
    public sealed class MemoryMappedPipeReader : PipeReader, IDisposable
    {
        private MemoryMappedFile _file;
        private readonly int _pageSize;

        /// <summary>
        /// Get a string representation of the object
        /// </summary>
        public override string ToString() => Name;

        private string Name { get; }
        [Conditional("VERBOSE")]
        private void DebugLog(string message, [CallerMemberName] string caller = null)
        {
#if VERBOSE
            Helpers.DebugLog(Name, message, caller);
#endif
        }

        private bool _loadMore = true;
        private long _remaining, _offset;
        private MappedPage _first, _last;

        /// <summary>
        /// Indicates whether this API is likely to work
        /// </summary>
        public static bool IsAvailable => s_safeBufferField != null;
        private static void AssertAvailable()
        {
            if (!IsAvailable) throw new PlatformNotSupportedException();
        }
        static readonly FieldInfo s_safeBufferField;
        static MemoryMappedPipeReader()
        {
            try
            {
                var fields = typeof(UnmanagedMemoryAccessor).GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                s_safeBufferField = fields.Single(x => x.FieldType == typeof(SafeBuffer));
            }
            catch (Exception ex)
            {
                Helpers.DebugLog(nameof(MemoryMappedPipeReader), ex.Message);
            }
        }
        private MemoryMappedPipeReader(MemoryMappedFile file, long length, int pageSize = DEFAULT_PAGE_SIZE, string name = null)
        {
            AssertAvailable();
            _file = file ?? throw new ArgumentNullException(nameof(file));
            if (pageSize <= 0) throw new ArgumentOutOfRangeException(nameof(pageSize));
            if (length < 0) throw new ArgumentOutOfRangeException(nameof(length));

            if (string.IsNullOrWhiteSpace(name)) name = GetType().Name;
            Name = name;
            _pageSize = pageSize;
            _remaining = length;
        }

        const int DEFAULT_PAGE_SIZE = 64 * 1024;
        /// <summary>
        /// Create a pipe-reader over the provided file
        /// </summary>
        public static PipeReader Create(string path, int pageSize = DEFAULT_PAGE_SIZE)
        {
            if (IsAvailable)
            {
                if (pageSize <= 0) throw new ArgumentOutOfRangeException(nameof(pageSize));
                var file = new FileInfo(path);
                if (!file.Exists) throw new FileNotFoundException();

                var mmap = MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, file.Length, MemoryMappedFileAccess.Read);
                return new MemoryMappedPipeReader(mmap, file.Length, pageSize, path);
            }
            else
            {
                // fallback... FileStream
                var file = File.OpenRead(path);
                return StreamConnector.GetReader(file, name: path);
            }
        }

        /// <summary>
        /// Mark the reader as complete
        /// </summary>
        /// <param name="exception"></param>
        public override void Complete(Exception exception = null) => Dispose();

        /// <summary>
        /// Releases all resources associated with the object
        /// </summary>
        public void Dispose()
        {
            var page = _first;
            while (page != null)
            {
                try { page.Dispose(); } catch { }
                page = page.Next;
            }
            _first = _last = null;
            try { _file?.Dispose(); } catch { }
            _file = null;
        }
        /// <summary>
        /// Not implemented
        /// </summary>
        public override void OnWriterCompleted(Action<Exception, object> callback, object state) { }
        /// <summary>
        /// Cancels an in-progress read
        /// </summary>
        public override void CancelPendingRead() { }

        /// <summary>
        /// Indicates how much data was consumed from a read operation
        /// </summary>
        public override void AdvanceTo(SequencePosition consumed) => AdvanceTo(consumed, consumed);
        /// <summary>
        /// Indicates how much data was consumed, and how much examined, from a read operation
        /// </summary>
        public override void AdvanceTo(SequencePosition consumed, SequencePosition examined)
        {
            var cPage = (MappedPage)consumed.GetObject();
            var ePage = (MappedPage)examined.GetObject();

            if (cPage == null || ePage == null)
            {
                if (_first == null) return; // that's fine - means they called Advance on an empty EOF
                throw new ArgumentException("Invalid position; consumed/examined must remain inside the buffer");
            }

            Debug.Assert(ePage != null, "No examined page");
            Debug.Assert(cPage != null, "No consumed page");

            MappedPage newKeep;
            var cOffset = consumed.GetInteger();
            if (cOffset == cPage.Capacity)
            {
                newKeep = cPage.Next;
            }
            else
            {
                newKeep = cPage;
                newKeep.Consumed = cOffset;
            }
            if (newKeep == null)
            {
                DebugLog($"Retaining nothing");
                _last = null;
            }
            else
            {
                DebugLog($"Retaining page {newKeep}");
            }

            // now drop any pages we don't need
            if (newKeep != _first)
            {
                var page = _first;
                while (page != null && page != newKeep)
                {
                    DebugLog($"Dropping page {page}");
                    page.Dispose();
                    page = page.Next;
                }
                _first = newKeep;
            }

            // check whether they looked at everything
            if (_last == null)
            {
                _loadMore = true; // definitely
            }
            else
            {
                var eOffset = examined.GetInteger();
                _loadMore = ePage == _last && eOffset == ePage.Capacity;
            }
        }
        /// <summary>
        /// Perform an asynchronous read operation
        /// </summary>
        public override ValueTask<ReadResult> ReadAsync(CancellationToken cancellationToken = default)
            => new ValueTask<ReadResult>(Read());
        /// <summary>
        /// Attempt to perfom a synchronous read operation
        /// </summary>
        public override bool TryRead(out ReadResult result)
        {
            result = Read();
            return true;
        }
        private ReadResult Read()
        {
            if (_loadMore)
            {
                if (_remaining != 0)
                {
                    var take = (int)Math.Min(_remaining, _pageSize);
                    DebugLog($"Loading next {take} bytes...");
                    var accessor = _file.CreateViewAccessor(_offset, take, MemoryMappedFileAccess.Read);
                    _remaining -= take;
                    _offset += take;
                    var next = new MappedPage(accessor, take);

                    if (_first == null)
                    {
                        _first = _last = next;
                    }
                    else
                    {
                        _last.Next = next;
                        _last = next;
                    }
                    DebugLog($"Loaded page {next}");
                }
                _loadMore = false;
            }

            if (_first == null)
            {
                Debug.Assert(_remaining == 0, "unexpected EOF");
                DebugLog($"Read has encountered EOF");
                return new ReadResult(default, false, true);
            }
            var buffer = new ReadOnlySequence<byte>(_first, _first.Consumed, _last, _last.Capacity);
            DebugLog($"Read making {buffer.Length} bytes available");
            return new ReadResult(buffer, false, _remaining != 0);
        }

        private sealed class MappedPage : ReadOnlySequenceSegment<byte>, IDisposable
        {
            public new MappedPage Next
            {
                get => (MappedPage)base.Next;
                set => base.Next = value;
            }

            private MemoryMappedViewAccessor _accessor;
            private SafeBuffer _buffer;
            public int Consumed { get; set; }
            public unsafe MappedPage(MemoryMappedViewAccessor accessor, int capacity)
            {
                _accessor = accessor ?? throw new ArgumentNullException(nameof(accessor));
                _buffer = s_safeBufferField.GetValue(_accessor) as SafeBuffer ?? throw new InvalidOperationException();
                // note that the *actual* capacity isn't necessarily the same - system page size (rounding up), etc
                if (capacity < 0 || capacity > accessor.Capacity) throw new ArgumentOutOfRangeException(nameof(capacity));
                RunningIndex = accessor.PointerOffset;
                byte* ptr = null;
                _buffer.AcquirePointer(ref ptr);
                Memory = new UnmanagedMemoryManager<byte>(ptr, capacity).Memory;
                Capacity = capacity;
            }
            public override string ToString() => $"[{(RunningIndex + Consumed)},{(RunningIndex + Capacity)})";
            public int Capacity { get; }
            public void Dispose()
            {
                Memory = default;
                _buffer.ReleasePointer();
                _accessor?.Dispose();
                _accessor = null;
            }
        }
    }
}
