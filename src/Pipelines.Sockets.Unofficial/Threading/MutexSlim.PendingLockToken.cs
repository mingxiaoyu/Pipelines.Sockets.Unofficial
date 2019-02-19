﻿using System;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace Pipelines.Sockets.Unofficial.Threading
{
    public partial class MutexSlim
    {
        internal abstract class AsyncPendingLockToken : PendingLockToken
        {
            protected MutexSlim Mutex { get; }
            protected AsyncPendingLockToken(MutexSlim mutex, uint start) : base(start) => Mutex = mutex;
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public LockToken GetResultAsToken() => new LockToken(Mutex, GetResult()).AssertNotCanceled();
            public abstract void OnCompleted(Action continuation);

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            protected void Schedule(Action<object> action, object state)
                => Mutex._scheduler.Schedule(action, state);

            public abstract ValueTask<LockToken> AsTask();
        }

        internal abstract class PendingLockToken
        {
            private int _token = LockState.Pending; // combined state and counter

            public uint Start { get; private set; } // for timeout tracking

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            protected void Reset(uint start)
            {
                Start = start;
                Volatile.Write(ref _token, LockState.Pending);
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            protected int VolatileStatus() => LockState.GetState(Volatile.Read(ref _token));

            protected PendingLockToken() { }
            protected PendingLockToken(uint start) => Start = start;

            public bool TrySetResult(int token)
            {
                int oldValue = Volatile.Read(ref _token);
                if (LockState.GetState(oldValue) == LockState.Pending
                    && Interlocked.CompareExchange(ref _token, token, oldValue) == oldValue)
                {
                    OnAssigned();
                    return true;
                }
                return false;
            }

            internal bool TryCancel()
            {
                int oldValue;
                do
                {
                    // depends on the current state...
                    oldValue = Volatile.Read(ref _token);
                    switch (LockState.GetState(oldValue))
                    {
                        case LockState.Canceled:
                            return true; // fine, already canceled
                        case LockState.Timeout:
                        case LockState.Success:
                            return false; // nope, already reported
                    }
                    // otherwise, attempt to change the field; in case of conflict; re-do from start
                } while (Interlocked.CompareExchange(ref _token, LockState.ChangeState(oldValue, LockState.Canceled), oldValue) != oldValue);
                OnAssigned();
                return true;
            }

            // if already complete: returns the token; otherwise, dooms the operation
            public int GetResult()
            {
                int oldValue, newValue;
                do
                {
                    oldValue = Volatile.Read(ref _token);
                    if (LockState.GetState(oldValue) != LockState.Pending)
                    {
                        // value is already fixed; just return it
                        return oldValue;
                    }
                    // we don't ever want to report different values from GetResult, so
                    // if you called GetResult prematurely: you doomed it to failure
                    newValue = LockState.ChangeState(oldValue, LockState.Timeout);

                    // if something changed while we were thinking, redo from start
                } while (Interlocked.CompareExchange(ref _token, newValue, oldValue) != oldValue);
                OnAssigned();
                return newValue;
            }

            public bool IsCompleted
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get { return LockState.IsCompleted(Volatile.Read(ref _token)); }
            }

            public bool IsCompletedSuccessfully
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get { return LockState.IsCompletedSuccessfully(Volatile.Read(ref _token)); }
            }

            public bool IsCanceled
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get { return LockState.IsCanceled(Volatile.Read(ref _token)); }
            }

            protected abstract void OnAssigned();
        }
    }
}
