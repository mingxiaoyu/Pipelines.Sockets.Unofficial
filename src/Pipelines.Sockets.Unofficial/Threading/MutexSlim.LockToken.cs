﻿using System;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace Pipelines.Sockets.Unofficial.Threading
{
    public partial class MutexSlim
    {
        /// <summary>
        /// The result of a Wait/WaitAsync operation on MutexSlim; the caller *must* check Success to see whether the mutex was obtained
        /// </summary>
        public readonly struct LockToken : IDisposable, IEquatable<LockToken>
        {
            /// <summary>
            /// Compare two LockToken instances for equality
            /// </summary>
            public static bool operator ==(LockToken x, LockToken y) => x.Equals(y);
            /// <summary>
            /// Compare two LockToken instances for equality
            /// </summary>
            public static bool operator !=(LockToken x, LockToken y) => !x.Equals(y);

            /// <summary>
            /// Compare two LockToken instances for equality
            /// </summary>
            public override bool Equals(object obj) => obj is LockToken other && Equals(other);

            /// <summary>
            /// See Object.GetHashCode
            /// </summary>
            public override int GetHashCode() => (_parent?.GetHashCode() ?? 0) ^ _token;

            /// <summary>
            /// See Object.ToString()
            /// </summary>
            public override string ToString() => nameof(LockToken);

            /// <summary>
            /// Compare two LockToken instances for equality
            /// </summary>
            public bool Equals(LockToken other)
            {
                if (_parent != null) return ReferenceEquals(_parent, other._parent);
                if (other._parent != null) return false;
                return _token == other._token;
            }

            private readonly MutexSlim _parent;
            private readonly int _token;

            /// <summary>
            /// Indicates whether the mutex was successfully taken
            /// </summary>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
#pragma warning disable RCS1231 // Make parameter ref read-only.
            public static bool operator true(LockToken token) => LockState.GetState(token._token) == LockState.Success;
#pragma warning restore RCS1231 // Make parameter ref read-only.
                               /// <summary>
                               /// Indicates whether the mutex was successfully taken
                               /// </summary>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
#pragma warning disable RCS1231 // Make parameter ref read-only.
            public static bool operator false(LockToken token) => LockState.GetState(token._token) != LockState.Success;
#pragma warning restore RCS1231 // Make parameter ref read-only.

            /// <summary>
            /// Indicates whether the mutex was successfully taken
            /// </summary>
            public bool Success => LockState.GetState(_token) == LockState.Success;

            internal bool IsCompleted => LockState.IsCompleted(_token);

            internal bool IsCompletedSuccessfully => LockState.IsCompletedSuccessfully(_token);

            internal bool IsCanceled => LockState.IsCanceled(_token);

            internal LockToken(MutexSlim parent, int token)
            {
                _parent = parent;
                _token = token;
            }

            /// <summary>
            /// Release the mutex, if obtained
            /// </summary>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void Dispose()
            {
                if (Success) _parent.Release(_token, demandMatch: true);
            }

            internal LockToken AssertNotCanceled()
            {
                if (IsCanceled) ThrowCanceled();
                return this;
                void ThrowCanceled() => throw new TaskCanceledException();
            }
        }
    }
}
