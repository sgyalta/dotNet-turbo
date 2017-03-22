﻿using Qoollo.Turbo.Queues.ServiceStuff;
using Qoollo.Turbo.Threading;
using Qoollo.Turbo.Threading.ThreadManagement;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Qoollo.Turbo.Queues
{
    /// <summary>
    /// Adding modes for LevelingQueue
    /// </summary>
    public enum LevelingQueueAddingMode
    {
        /// <summary>
        /// Indicates that the ordering of data is critical
        /// </summary>
        PreserveOrder = 0,
        /// <summary>
        /// Indicates that data should be first added to the HighLevelQueue if possible
        /// </summary>
        PreferLiveData
    }


    /// <summary>
    /// Queue that controls two queues. 
    /// One of the higher level and another as the backing storage when first is full.
    /// </summary>
    /// <example>
    /// Helps to combine small and fast queue in memory with large and slow queue on disk
    /// </example>
    /// <typeparam name="T">The type of elements in queue</typeparam>
    public class LevelingQueue<T> : Common.CommonQueueImpl<T>, IDisposable
    {
        /// <summary>
        /// As long as the inner queue possibly can be changed outside we use Polling on MonitorObject with reasonable WaitPollingTimeout
        /// </summary>
        private const int WaitPollingTimeout = 2000;

        private static readonly int ProcessorCount = Environment.ProcessorCount;

        private readonly IQueue<T> _highLevelQueue;
        private readonly IQueue<T> _lowLevelQueue;

        private readonly LevelingQueueAddingMode _addingMode;
        private readonly bool _isBackgroundTransferingEnabled;

        private readonly MonitorObject _addMonitor;
        private readonly MonitorObject _takeMonitor;

        private readonly DelegateThreadSetManager _backgroundTransferer;
        private readonly MutuallyExclusiveProcessPrimitive _bacgoundTransfererExclusive;

        private volatile bool _isDisposed;

        public LevelingQueue(IQueue<T> highLevelQueue, IQueue<T> lowLevelQueue, LevelingQueueAddingMode addingMode, bool isBackgroundTransferingEnabled)
        {
            if (highLevelQueue == null)
                throw new ArgumentNullException(nameof(highLevelQueue));
            if (lowLevelQueue == null)
                throw new ArgumentNullException(nameof(lowLevelQueue));

            _highLevelQueue = highLevelQueue;
            _lowLevelQueue = lowLevelQueue;

            _addingMode = addingMode;
            _isBackgroundTransferingEnabled = isBackgroundTransferingEnabled;

            _addMonitor = new MonitorObject("AddMonitor");
            _takeMonitor = new MonitorObject("TakeMonitor");

            if (isBackgroundTransferingEnabled)
            {
                _bacgoundTransfererExclusive = new MutuallyExclusiveProcessPrimitive();
                _backgroundTransferer = new DelegateThreadSetManager(1, this.GetType().GetCSName() + "_" + this.GetHashCode().ToString(), BackgroundTransferProc);
                _backgroundTransferer.IsBackground = true;
                _backgroundTransferer.Start();
            }

            _isDisposed = false;
        }

        /// <summary>
        /// Direct access to high level queue
        /// </summary>
        protected IQueue<T> HighLevelQueue { get { return _highLevelQueue; } }
        /// <summary>
        /// Direct access to low level queue
        /// </summary>
        protected IQueue<T> LowLevelQueue { get { return _lowLevelQueue; } }
        /// <summary>
        /// Adding mode of the queue
        /// </summary>
        public LevelingQueueAddingMode AddingMode { get { return _addingMode; } }
        /// <summary>
        /// Is transfering items from LowLevelQueue to HighLevelQueue in background enabled
        /// </summary>
        public bool IsBackgroundTransferingEnabled { get { return _isBackgroundTransferingEnabled; } }

        /// <summary>
        /// The bounded size of the queue (-1 means not bounded)
        /// </summary>
        public sealed override long BoundedCapacity
        {
            get
            {
                long lowLevelBoundedCapacity = _lowLevelQueue.BoundedCapacity;
                if (lowLevelBoundedCapacity < 0)
                    return -1;
                long highLevelBoundedCapacity = _highLevelQueue.BoundedCapacity;
                if (highLevelBoundedCapacity < 0)
                    return -1;
                return highLevelBoundedCapacity + lowLevelBoundedCapacity;
            }
        }

        /// <summary>
        /// Number of items inside the queue
        /// </summary>
        public sealed override long Count
        {
            get
            {
                long highLevelCount = _highLevelQueue.Count;
                if (highLevelCount < 0)
                    return -1;
                long lowLevelCount = _lowLevelQueue.Count;
                if (lowLevelCount < 0)
                    return -1;
                return highLevelCount + lowLevelCount;
            }
        }

        /// <summary>
        /// Indicates whether the queue is empty
        /// </summary>
        public sealed override bool IsEmpty { get { return _highLevelQueue.IsEmpty && _lowLevelQueue.IsEmpty; } }


        /// <summary>
        /// Wait handle that notifies about items presence
        /// </summary>
        protected sealed override WaitHandle HasItemsWaitHandle { get { throw new NotImplementedException(); } }
        /// <summary>
        /// Wait handle that notifies about space availability for new items
        /// </summary>
        protected sealed override WaitHandle HasSpaceWaitHandle { get { throw new NotImplementedException(); } }


        /// <summary>
        /// Checks if queue is disposed
        /// </summary>
        private void CheckDisposed()
        {
            if (_isDisposed)
                throw new ObjectDisposedException(this.GetType().Name);
        }

        /// <summary>
        /// Adds new item to the queue, even when the bounded capacity reached
        /// </summary>
        /// <param name="item">New item</param>
        public override void AddForced(T item)
        {
            CheckDisposed();

            if (_addingMode == LevelingQueueAddingMode.PreferLiveData)
            {
                if (!_highLevelQueue.TryAdd(item, 0, default(CancellationToken)))
                    _lowLevelQueue.AddForced(item);
            }
            else
            {
                if (!_lowLevelQueue.IsEmpty || !_highLevelQueue.TryAdd(item, 0, default(CancellationToken)))
                    _lowLevelQueue.AddForced(item);
            }

            _takeMonitor.Pulse();
        }

        /// <summary>
        /// Adds new item to the high level queue, even when the bounded capacity reached
        /// </summary>
        /// <param name="item">New item</param>
        public void AddForcedToHighLevelQueue(T item)
        {
            CheckDisposed();
            _highLevelQueue.AddForced(item);
            _takeMonitor.Pulse();
        }


        // ====================== Add ==================

        /// <summary>
        /// Check wheter the value inside specified interval (inclusively)
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool IsInsideInterval(long value, long min, long max)
        {
            return value >= min && value <= max;
        }

        /// <summary>
        /// Fast path to add the item (with zero timeout)
        /// </summary>
        /// <param name="item">New item</param>
        /// <returns>Was added sucessufully</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool TryAddFast(T item)
        {
            Debug.Assert(_addingMode == LevelingQueueAddingMode.PreferLiveData, "Only PreferLiveData supported");

            if (_highLevelQueue.TryAdd(item, 0, default(CancellationToken)))
                return true;
            if (_lowLevelQueue.TryAdd(item, 0, default(CancellationToken)))
                return true;

            return false;
        }

        /// <summary>
        /// Slow path to add the item (waits on <see cref="_addMonitor"/>)
        /// </summary>
        /// <param name="item">New item</param>
        /// <param name="timeout">Timeout</param>
        /// <param name="token">Token</param>
        /// <returns>Was added sucessufully</returns>
        private bool TryAddSlow(T item, int timeout, CancellationToken token)
        {
            using (var waiter = _addMonitor.Enter(timeout, token))
            {
                if (TryAddFast(item))
                    return true;

                while (!waiter.IsTimeouted)
                {
                    waiter.Wait(WaitPollingTimeout);
                    if (TryAddFast(item))
                        return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Adds new item to the tail of the queue (core method)
        /// </summary>
        /// <param name="item">New item</param>
        /// <param name="timeout">Adding timeout</param>
        /// <param name="token">Cancellation token</param>
        /// <returns>Was added sucessufully</returns>
        protected override bool TryAddCore(T item, int timeout, CancellationToken token)
        {
            CheckDisposed();

            if (token.IsCancellationRequested)
                throw new OperationCanceledException(token);

            bool result = false;

            if (_addingMode == LevelingQueueAddingMode.PreferLiveData)
            {
                result = _addMonitor.WaiterCount == 0 && TryAddFast(item);
                if (!result && timeout != 0)
                    result = TryAddSlow(item, timeout, token); // Use slow path to add to any queue
            }
            else
            {
                if (_isBackgroundTransferingEnabled && !_lowLevelQueue.IsEmpty && IsInsideInterval(_lowLevelQueue.Count, 0, ProcessorCount))
                {
                    // Attempt to wait for lowLevelQueue to become empty
                    SpinWait sw = new SpinWait();
                    while (!sw.NextSpinWillYield && !_lowLevelQueue.IsEmpty)
                        sw.SpinOnce();
                    if (sw.NextSpinWillYield && timeout != 0 && !_lowLevelQueue.IsEmpty)
                        sw.SpinOnce();
                }

                result = _lowLevelQueue.IsEmpty && _highLevelQueue.TryAdd(item, 0, default(CancellationToken));
                if (!result)
                    result = _lowLevelQueue.TryAdd(item, timeout, token); // To preserve order we try to add only to the lower queue
            }

            if (result)
                _takeMonitor.Pulse();

            return result;
        }


        // ========================== Take ==================

        /// <summary>
        /// Fast path to take the item from any queue (with zero timeout)
        /// </summary>
        /// <param name="item">The item removed from queue</param>
        /// <returns>True if the item was removed</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool TryTakeFast(out T item)
        {
            if (_highLevelQueue.TryTake(out item, 0, default(CancellationToken)))
                return true;
            if (_lowLevelQueue.TryTake(out item, 0, default(CancellationToken)))
                return true;

            return false;
        }
        /// <summary>
        /// Slow path to take the item from queue (uses <see cref="_takeMonitor"/>)
        /// </summary>
        /// <param name="item">The item removed from queue</param>
        /// <param name="timeout">Timeout</param>
        /// <param name="token">Token</param>
        /// <returns>True if the item was removed</returns>
        private bool TryTakeSlow(out T item, int timeout, CancellationToken token)
        {
            using (var waiter = _takeMonitor.Enter(timeout, token))
            {
                if (TryTakeFast(out item))
                    return true;

                while (!waiter.IsTimeouted)
                {
                    waiter.Wait(WaitPollingTimeout);
                    if (TryTakeFast(out item))
                        return true;
                }
            }

            return false;
        }

        private bool TryTakeExclusively(out T item, int timeout, CancellationToken token)
        {
            Debug.Assert(_isBackgroundTransferingEnabled && _addingMode == LevelingQueueAddingMode.PreserveOrder);

            _bacgoundTransfererExclusive.RequestGate1Open(); // Open current gate
            using (var gateGuard = _bacgoundTransfererExclusive.EnterGate1(Timeout.Infinite, token)) // This should happen fast
            {
                Debug.Assert(gateGuard.IsAcquired);

                if (TryTakeFast(out item))
                    return true;

                return TryTakeSlow(out item, timeout, token);
            }
        }

        /// <summary>
        /// Removes item from the head of the queue (core method)
        /// </summary>
        /// <param name="item">The item removed from queue</param>
        /// <param name="timeout">Removing timeout</param>
        /// <param name="token">Cancellation token</param>
        /// <returns>True if the item was removed</returns>
        protected override bool TryTakeCore(out T item, int timeout, CancellationToken token)
        {
            CheckDisposed();

            bool result = false;
            item = default(T);

            if (_isBackgroundTransferingEnabled && _addingMode == LevelingQueueAddingMode.PreserveOrder)
            {
                result = _highLevelQueue.TryTake(out item, 0, default(CancellationToken));
                if (!result)
                    result = TryTakeExclusively(out item, timeout, token); // Should be mutually exclusive with background transferer
                else if (!_lowLevelQueue.IsEmpty)
                    _bacgoundTransfererExclusive.RequestGate2Open(); // allow Background transfering
            }
            else
            {
                result = _takeMonitor.WaiterCount == 0 && TryTakeFast(out item);
                if (!result)
                    result = TryTakeSlow(out item, timeout, token);
            }

            if (result)
                _addMonitor.Pulse();

            return result;
        }


        // ===================== Peek ===============


        /// <summary>
        /// Returns the item at the head of the queue without removing it (core method)
        /// </summary>
        /// <param name="item">The item at the head of the queue</param>
        /// <param name="timeout">Peeking timeout</param>
        /// <param name="token">Cancellation token</param>
        /// <returns>True if the item was read</returns>
        protected override bool TryPeekCore(out T item, int timeout, CancellationToken token)
        {
            CheckDisposed();

            throw new NotImplementedException();
        }

        // ====================== Background ===============


        /// <summary>
        /// Transfers data from LowLevelQueue to HighLevelQueue in background
        /// </summary>
        private void BackgroundTransferProc(object state, CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                using (var gateGuard = _bacgoundTransfererExclusive.EnterGate2(Timeout.Infinite, token)) // Background is on Gate2
                using (var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(token, gateGuard.Token))
                {
                    Debug.Assert(gateGuard.IsAcquired);

                    T item = default(T);
                    bool itemTaken = false;
                    try
                    {
                        while (!linkedCancellation.IsCancellationRequested)
                        {
                            item = default(T);
                            if (itemTaken = _lowLevelQueue.TryTake(out item, Timeout.Infinite, linkedCancellation.Token))
                            {
                                if (!_highLevelQueue.TryAdd(item, 0, default(CancellationToken))) // Fast path to ignore cancellation
                                {
                                    bool itemAdded = _highLevelQueue.TryAdd(item, Timeout.Infinite, linkedCancellation.Token);
                                    Debug.Assert(itemAdded);
                                }

                                itemTaken = false;
                                _takeMonitor.Pulse();
                            }
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        if (itemTaken)
                        {
                            _highLevelQueue.AddForced(item); // Prevent item lost
                            itemTaken = false;
                            _takeMonitor.Pulse();
                        }

                        if (!linkedCancellation.IsCancellationRequested)
                            throw;
                    }
                }
            }
        }


        /// <summary>
        /// Clean-up all resources
        /// </summary>
        /// <param name="isUserCall">Was called by user</param>
        protected override void Dispose(bool isUserCall)
        {
            if (!_isDisposed)
            {
                _isDisposed = true;

                if (_backgroundTransferer != null)
                    _backgroundTransferer.Stop(waitForStop: true);

                _addMonitor.Dispose();
                _takeMonitor.Dispose();

                _lowLevelQueue.Dispose();
                _highLevelQueue.Dispose();

                if (_bacgoundTransfererExclusive != null)
                    _bacgoundTransfererExclusive.Dispose();
            }
        }
    }
}
