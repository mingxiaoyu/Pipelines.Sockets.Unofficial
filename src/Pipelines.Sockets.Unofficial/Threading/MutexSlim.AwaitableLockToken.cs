﻿using System;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace Pipelines.Sockets.Unofficial.Threading
{
    public partial class MutexSlim
    {
        /// <summary>
        /// Custom awaitable result from WaitAsync on MutexSlim
        /// </summary>
        public readonly struct AwaitableLockToken : INotifyCompletion, ICriticalNotifyCompletion, IEquatable<AwaitableLockToken>
        {
            /// <summary>
            /// Compare two AwaitableLockToken instances for equality
            /// </summary>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public override bool Equals(object obj) => obj is AwaitableLockToken other && Equals(other);

            /// <summary>
            /// See Object.GetHashCode
            /// </summary>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public override int GetHashCode() => _pending == null ? _token.GetHashCode() : _pending.GetHashCode();

            /// <summary>
            /// Compare two AwaitableLockToken instances for equality
            /// </summary>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public bool Equals(AwaitableLockToken other)
            {
                if (_pending != null) return ReferenceEquals(_pending, other._pending);
                if (other._pending != null) return false;
                return _token.Equals(other._token);
            }

            /// <summary>
            /// See Object.ToString()
            /// </summary>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public override string ToString() => nameof(AwaitableLockToken);

            /// <summary>
            /// Compare two AwaitableLockToken instances for equality
            /// </summary>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static bool operator ==(AwaitableLockToken x, AwaitableLockToken y) => x.Equals(y);
            /// <summary>
            /// Compare two AwaitableLockToken instances for equality
            /// </summary>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static bool operator !=(AwaitableLockToken x, AwaitableLockToken y) => !x.Equals(y);

            private readonly AsyncPendingLockToken _pending;
            private readonly LockToken _token;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal static AwaitableLockToken Canceled() => new AwaitableLockToken(new LockToken(null, LockState.Canceled));

#pragma warning disable RCS1231 // Make parameter ref read-only.
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal AwaitableLockToken(LockToken token)
#pragma warning restore RCS1231 // Make parameter ref read-only.
            {
                _token = token;
                _pending = default;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal AwaitableLockToken(AsyncPendingLockToken pending)
            {
                _token = default;
                _pending = pending;
            }

            /// <summary>
            /// Obtain the LockToken after completion of this async operation
            /// </summary>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public LockToken GetResult() => _pending?.GetResultAsToken() ?? _token.AssertNotCanceled();

            /// <summary>
            /// Obtain the awaiter associated with this awaitable result
            /// </summary>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public AwaitableLockToken GetAwaiter() => this;

            /// <summary>
            /// Schedule a continuation to be invoked after completion of this async operation
            /// </summary>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void OnCompleted(Action continuation)
            {
                if (continuation != null)
                {
                    if (_pending == null) continuation(); // already complete; invoke directly
                    else _pending.OnCompleted(continuation);
                }
            }

            /// <summary>
            /// Schedule a continuation to be invoked after completion of this async operation
            /// </summary>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void UnsafeOnCompleted(Action continuation) => OnCompleted(continuation);

            /// <summary>
            /// Attempt to cancel an incomplete pending operation; once successfully canceled,
            /// there is no obligation to check for and dispose the LockToken
            /// </summary>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal bool TryCancel() => _pending?.TryCancel() ?? _token.IsCanceled;

            /// <summary>
            /// Indicates whether this awaitable result has completed
            /// </summary>
            public bool IsCompleted
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get { return _pending?.IsCompleted ?? _token.IsCompleted; }
            }

            /// <summary>
            /// Indicates whether this awaitable result has completed without cancelation or faulting
            /// </summary>
            public bool IsCompletedSuccessfully
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get { return _pending?.IsCompletedSuccessfully ?? _token.IsCompletedSuccessfully; }
            }

            /// <summary>
            /// Indicates whether the async operation was canceled
            /// </summary>
            public bool IsCanceled
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get { return _pending?.IsCanceled ?? _token.IsCanceled; }
            }

            /// <summary>
            /// Indicates whether this awaitable result completed without an asynchronous step
            /// </summary>
            public bool CompletedSynchronously
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get { return _pending == null; }
            }

            /// <summary>
            /// Exposes the async operation as a ValueTask - note that this requires AsTask to be supported for full support
            /// </summary>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public ValueTask<LockToken> AsTask()
            {
                if (_pending != null) return _pending.AsTask();
                if (_token.IsCanceled) return GetCanceled();
                return new ValueTask<LockToken>(_token);
            }

            /// <summary>
            /// Convert an AwaitableLockToken to a ValueTask
            /// </summary>
            public static implicit operator ValueTask<LockToken>(AwaitableLockToken token) => token.AsTask();

            static Task<LockToken> s_canceledTask;
            internal static ValueTask<LockToken> GetCanceled() => new ValueTask<LockToken>(s_canceledTask ?? (s_canceledTask = CreateCanceledLockTokenTask()));
            private static Task<LockToken> CreateCanceledLockTokenTask()
            {
                var tcs = new TaskCompletionSource<LockToken>();
                tcs.SetCanceled();
                return tcs.Task;
            }
        }
    }
}
