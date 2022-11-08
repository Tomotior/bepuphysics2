﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Dynamic;
using System.Linq;
using System.Numerics;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BepuUtilities.Memory;

namespace BepuUtilities;

/// <summary>
/// Description of one task within a job to be submitted to a <see cref="TaskQueue"/>.
/// </summary>
public unsafe struct Task
{
    /// <summary>
    /// Function to be executed by the task. Takes as arguments the <see cref="TaskId"/>, <see cref="Context"/> pointer, and executing worker index.
    /// </summary>
    public delegate*<int, void*, int, void> Function;
    /// <summary>
    /// Context to be passed into the <see cref="Function"/>.
    /// </summary>
    public void* Context;
    /// <summary>
    /// Identifier of this task within the job.
    /// </summary>
    public int TaskId;
}

/// <summary>
/// Describes the result status of a dequeue attempt.
/// </summary>
public enum DequeueTaskResult
{
    /// <summary>
    /// A task was successfully dequeued.
    /// </summary>
    Success,
    /// <summary>
    /// The queue was empty, but may have more tasks in the future.
    /// </summary>
    Empty,
    /// <summary>
    /// The queue has been terminated and all threads seeking work should stop.
    /// </summary>
    Stop
}

/// <summary>
/// Describes the result of a task enqueue attempt.
/// </summary>
public enum EnqueueTaskResult
{
    /// <summary>
    /// The tasks were successfully enqueued.
    /// </summary>
    Success,
    /// <summary>
    /// The enqueue attempt was blocked by concurrent access.
    /// </summary>
    Contested,
    /// <summary>
    /// The enqueue attempt was blocked because no space remained in the tasks buffer.
    /// </summary>
    Full,
}

/// <summary>
/// Refers to a continuation within a <see cref="TaskQueue"/>.
/// </summary>
public struct ContinuationHandle : IEquatable<ContinuationHandle>
{
    uint index;
    uint encodedVersion;

    internal ContinuationHandle(uint index, int version)
    {
        this.index = index;
        encodedVersion = (uint)version | 1u << 31;
    }

    internal uint Index
    {
        get
        {
            Debug.Assert(Initialized, "If you're trying to pull a continuation id from a continuation handle, it should have been initialized.");
            return index;
        }
    }

    internal int Version
    {
        get
        {
            Debug.Assert(Initialized, "If you're trying to pull a continuation id from a continuation handle, it should have been initialized.");
            return (int)(encodedVersion & ((1u << 31) - 1));
        }
    }

    /// <summary>
    /// Gets a null continuation handle.
    /// </summary>
    public static ContinuationHandle Null => default;

    /// <summary>
    /// Gets whether this handle was ever initialized. This does not guarantee that the job handle is active in the <see cref="TaskQueue"/> that it was allocated from.
    /// </summary>
    public bool Initialized => encodedVersion >= 1u << 31;

    public bool Equals(ContinuationHandle other) => other.index == index && other.encodedVersion == encodedVersion;

    public override bool Equals([NotNullWhen(true)] object obj) => obj is ContinuationHandle handle && Equals(handle);

    public override int GetHashCode() => (int)(index ^ (encodedVersion << 24));

    public static bool operator ==(ContinuationHandle left, ContinuationHandle right) => left.Equals(right);

    public static bool operator !=(ContinuationHandle left, ContinuationHandle right) => !(left == right);
}

/// <summary>
/// Describes the result of a continuation allocation attempt.
/// </summary>
public enum AllocateTaskContinuationResult
{
    /// <summary>
    /// The continuation was successfully allocated.
    /// </summary>
    Success,
    /// <summary>
    /// The continuation was blocked by concurrent access.
    /// </summary>
    Contested,
    /// <summary>
    /// The queue's continuation buffer is full and can't hold the continuation.
    /// </summary>
    Full
}

internal unsafe struct TaskQueueContinuations
{
    public Buffer<TaskContinuation> Continuations;
    public IdPool IndexPool;
    public int ContinuationCount;
    public int Locker;

    /// <summary>
    /// Retrieves a pointer to the continuation data for <see cref="ContinuationHandle"/>.
    /// </summary>
    /// <param name="continuationHandle">Handle to look up the associated continuation for.</param>
    /// <returns>Pointer to the continuation backing the given handle.</returns>
    /// <remarks>This should not be used if the continuation handle is not known to be valid. The data pointed to by the data could become invalidated if the continuation completes.</remarks>
    public TaskContinuation* GetContinuation(ContinuationHandle continuationHandle)
    {
        Debug.Assert(continuationHandle.Initialized, "This continuation handle was never initialized.");
        Debug.Assert(continuationHandle.Index < Continuations.length, "This continuation refers to an invalid index.");
        if (continuationHandle.Index >= Continuations.length || !continuationHandle.Initialized)
            return null;
        var continuation = Continuations.Memory + continuationHandle.Index;
        Debug.Assert(continuation->Version == continuationHandle.Version, "This continuation no longer refers to an active continuation.");
        if (continuation->Version != continuationHandle.Version)
            return null;
        return Continuations.Memory + continuationHandle.Index;
    }
}

/// <summary>
/// Wraps a task for easier use with continuations.
/// </summary>
public unsafe struct WrappedTaskContext
{
    /// <summary>
    /// Function to be invoked by this wrapped tsak.
    /// </summary>
    public delegate*<int, void*, int, void> Function;
    /// <summary>
    /// Context to be passed to this wrapped task.
    /// </summary>
    public void* Context;
    /// <summary>
    /// Handle of the continuation associated with this wrapped task.
    /// </summary>
    public ContinuationHandle Continuation;
    /// <summary>
    /// Set of continuations in the queue.
    /// </summary>
    internal TaskQueueContinuations* Continuations;
}

/// <summary>
/// Stores data relevant to tracking task completion and reporting completion for a job.
/// </summary>
public unsafe struct TaskContinuation
{
    /// <summary>
    /// Function to call upon completion of the job, if any.
    /// </summary>
    public delegate*<ulong, void*, int, void> OnCompleted;
    /// <summary>
    /// Context to pass to the completion function, if any.
    /// </summary>
    public void* OnCompletedContext;
    internal TaskQueueContinuations* Continuations;
    /// <summary>
    /// Id provided by the user to identify this job.
    /// </summary>
    public ulong UserId;
    /// <summary>
    /// Version of this continuation.
    /// </summary>
    public int Version;
    /// <summary>
    /// Number of tasks not yet reported as complete in the job.
    /// </summary>
    public int RemainingTaskCounter;
}


/// <summary>
/// Multithreaded task queue 
/// </summary>
public unsafe struct TaskQueue
{
    Buffer<Task> tasks;

    int taskMask, taskShift;

    //TODO: Careful about false sharing.
    long taskIndex;
    long allocatedTaskIndex;
    long writtenTaskIndex;

    volatile int taskLocker;

    /// <summary>
    /// Holds the task queue's continuations data in unmanaged memory just in case the queue itself is in unpinned memory.
    /// </summary>
    Buffer<TaskQueueContinuations> continuationsContainer;

    /// <summary>
    /// Constructs a new task queue.
    /// </summary>
    /// <param name="pool">Buffer pool to allocate resources from.</param>
    /// <param name="maximumTaskCapacity">Maximum number of tasks to allocate space for. Tasks are individual chunks of scheduled work. Rounded up to the nearest power of 2.</param>
    /// <param name="maximumContinuationCapacity">Maximum number of continuations to allocate space for. If more continuations exist at any one moment, attempts to create new continuations may have to stall until space is available.</param>
    public TaskQueue(BufferPool pool, int maximumTaskCapacity = 1024, int maximumContinuationCapacity = 256)
    {
        maximumTaskCapacity = (int)BitOperations.RoundUpToPowerOf2((uint)maximumTaskCapacity);
        pool.Take(1, out continuationsContainer);
        ref var continuations = ref continuationsContainer[0];
        pool.Take(maximumContinuationCapacity, out continuations.Continuations);
        continuations.IndexPool = new IdPool(maximumContinuationCapacity, pool);
        continuations.ContinuationCount = 0;
        continuations.Locker = 0;

        pool.Take(maximumTaskCapacity, out tasks);
        taskMask = tasks.length - 1;
        taskShift = BitOperations.TrailingZeroCount(tasks.length);
        taskLocker = 0;
        Reset();
    }

    /// <summary>
    /// Returns the task queue to a fresh state without reallocating.
    /// </summary>
    public void Reset()
    {
        taskIndex = 0;
        allocatedTaskIndex = 0;
        writtenTaskIndex = 0;
        Debug.Assert(taskLocker == 0, "There appears to be a thread actively working still. That's invalid.");

        ref var continuations = ref continuationsContainer[0];
        continuations.Continuations.Clear(0, continuations.Continuations.Length);
        continuations.ContinuationCount = 0;
        Debug.Assert(continuations.Locker == 0, "There appears to be a thread actively working still. That's invalid.");
    }

    /// <summary>
    /// Returns unmanaged resources held by the <see cref="TaskQueue"/> to a pool.
    /// </summary>
    /// <param name="pool">Buffer pool to return resources to.</param>
    public void Dispose(BufferPool pool)
    {
        continuationsContainer[0].IndexPool.Dispose(pool);
        pool.Return(ref continuationsContainer[0].Continuations);
        pool.Return(ref tasks);
        pool.Return(ref continuationsContainer);
    }

    /// <summary>
    /// Gets the queue's capacity for tasks.
    /// </summary>
    public int TaskCapacity => tasks.length;
    /// <summary>
    /// Gets the queue's capacity for continuations.
    /// </summary>
    public int ContinuationCapacity => continuationsContainer[0].Continuations.length;
    /// <summary>
    /// Gets the number of tasks active in the queue.
    /// </summary>
    /// <remarks>Does not take a lock; if other threads are modifying the task values, the reported count may be invalid.</remarks>
    public int UnsafeTaskCount => (int)(writtenTaskIndex - taskIndex);
    /// <summary>
    /// Gets the number of tasks active in the queue.
    /// </summary>
    public int TaskCount
    {
        get
        {
            var waiter = new SpinWait();
            while (Interlocked.CompareExchange(ref taskLocker, 1, 0) != 0)
            {
                waiter.SpinOnce(-1);
            }
            var result = (int)(writtenTaskIndex - taskIndex);
            taskLocker = 0;
            return result;
        }
    }
    /// <summary>
    /// Gets the number of continuations active in the queue.
    /// </summary>
    public int ContinuationCount => continuationsContainer[0].ContinuationCount;

    /// <summary>
    /// Attempts to dequeue a task.
    /// </summary>
    /// <param name="function">Function associated with the dequeued task, if any.</param>
    /// <param name="context">Context pointer associated with the dequeued task, if any.</param>
    /// <param name="taskId">Task id associated with the dequeued task, if any.</param>
    /// <returns>Result status of the dequeue attempt.</returns>
    public DequeueTaskResult TryDequeue(out delegate*<int, void*, int, void> function, out void* context, out int taskId)
    {
        function = default;
        context = default;
        taskId = default;
        while (true)
        {
            long nextTaskIndex, sampledWrittenTaskIndex;
            //Note that there is no lock taken. We sample the currently visible values and treat the dequeue as a transaction.
            //If the transaction fails, we don't make any changes and try again.
            if (Environment.Is64BitProcess) //This branch is compile time (at least where I've tested it).
            {
                //It's fine if we don't get a consistent view of the task index and written task index. Worst case scenario, this will claim that the queue is empty where a lock wouldn't.
                nextTaskIndex = Volatile.Read(ref taskIndex);
                sampledWrittenTaskIndex = Volatile.Read(ref writtenTaskIndex);
            }
            else
            {
                //Interlocked reads for 32 bit systems.
                nextTaskIndex = Interlocked.Read(ref taskIndex);
                sampledWrittenTaskIndex = Interlocked.Read(ref writtenTaskIndex);
            }
            if (nextTaskIndex >= sampledWrittenTaskIndex)
                return DequeueTaskResult.Empty;
            var task = tasks[(int)(nextTaskIndex & taskMask)];
            if (task.Function == null)
                return DequeueTaskResult.Stop;
            //Unlike enqueues, a dequeue has a fixed contention window on a single value. There's not much point in using a spinwait when there's no reason to expect our next attempt to be blocked.
            if (Interlocked.CompareExchange(ref taskIndex, nextTaskIndex + 1, nextTaskIndex) != nextTaskIndex)
                continue;
            //There's an actual job!
            function = task.Function;
            context = task.Context;
            taskId = task.TaskId;
            return DequeueTaskResult.Success;
        }
    }

    /// <summary>
    /// Attempts to dequeue a task and run it.
    /// </summary>
    /// <param name="workerIndex">Index of the worker to pass into the task function.</param>
    /// <returns>Result status of the dequeue attempt.</returns>
    public DequeueTaskResult TryDequeueAndRun(int workerIndex)
    {
        var result = TryDequeue(out var function, out var context, out var taskId);
        if (result == DequeueTaskResult.Success)
            function(taskId, context, workerIndex);
        return result;
    }

    /// <summary>
    /// Checks whether all tasks composing a job, as reported to the continuation, have completed.
    /// </summary>
    /// <param name="continuationHandle">Job to check for completion.</param>
    /// <returns>True if the job has completed, false otherwise.</returns>
    public bool IsComplete(ContinuationHandle continuationHandle)
    {
        Debug.Assert(continuationHandle.Initialized, "This continuation handle was never initialized.");
        Debug.Assert(continuationHandle.Index < continuationsContainer[0].Continuations.length, "This continuation refers to an invalid index.");
        ref var continuationSet = ref continuationsContainer[0];
        if (continuationHandle.Index >= continuationSet.Continuations.length || !continuationHandle.Initialized)
            return false;
        ref var continuation = ref continuationSet.Continuations[continuationHandle.Index];
        return continuation.Version > continuationHandle.Version || continuation.RemainingTaskCounter == 0;
    }
    /// <summary>
    /// Retrieves a pointer to the continuation data for <see cref="ContinuationHandle"/>.
    /// </summary>
    /// <param name="continuationHandle">Handle to look up the associated continuation for.</param>
    /// <returns>Pointer to the continuation backing the given handle.</returns>
    /// <remarks>This should not be used if the continuation handle is not known to be valid. The data pointed to by the data could become invalidated if the continuation completes.</remarks>
    public TaskContinuation* GetContinuation(ContinuationHandle continuationHandle)
    {
        return continuationsContainer[0].GetContinuation(continuationHandle);
    }

    EnqueueTaskResult TryEnqueueTasksUnsafelyInternal(Span<Task> tasks, out long taskEndIndex)
    {
        Debug.Assert(tasks.Length > 0, "Probably shouldn't be trying to enqueue zero tasks.");
        Debug.Assert(writtenTaskIndex == 0 || this.tasks[(int)((writtenTaskIndex - 1) & taskMask)].Function != null, "No more jobs should be written after a stop command.");
        var taskStartIndex = allocatedTaskIndex;
        taskEndIndex = taskStartIndex + tasks.Length;
        if (taskEndIndex - taskIndex > this.tasks.length)
        {
            //We've run out of space in the ring buffer. If we tried to write, we'd overwrite jobs that haven't yet been completed.
            return EnqueueTaskResult.Full;
        }
        allocatedTaskIndex = taskEndIndex;
        Debug.Assert(BitOperations.IsPow2(this.tasks.Length));
        var wrappedInclusiveStartIndex = (int)(taskStartIndex & taskMask);
        var wrappedInclusiveEndIndex = (int)(taskEndIndex & taskMask);
        if (wrappedInclusiveEndIndex > wrappedInclusiveStartIndex)
        {
            //We can just copy the whole task block as one blob.
            Unsafe.CopyBlockUnaligned(ref *(byte*)(this.tasks.Memory + taskStartIndex), ref Unsafe.As<Task, byte>(ref MemoryMarshal.GetReference(tasks)), (uint)(Unsafe.SizeOf<Task>() * tasks.Length));
        }
        else
        {
            //Copy the task block as two blobs.
            ref var startTask = ref tasks[0];
            var firstRegionCount = this.tasks.length - wrappedInclusiveStartIndex;
            ref var secondBlobStartTask = ref tasks[firstRegionCount];
            var secondRegionCount = tasks.Length - firstRegionCount;
            Unsafe.CopyBlockUnaligned(ref *(byte*)(this.tasks.Memory + taskStartIndex), ref Unsafe.As<Task, byte>(ref startTask), (uint)(Unsafe.SizeOf<Task>() * firstRegionCount));
            Unsafe.CopyBlockUnaligned(ref *(byte*)this.tasks.Memory, ref Unsafe.As<Task, byte>(ref secondBlobStartTask), (uint)(Unsafe.SizeOf<Task>() * secondRegionCount));
        }
        //for (int i = 0; i < tasks.Length; ++i)
        //{
        //    var taskIndex = (int)((i + taskStartIndex) & taskMask);
        //    this.tasks[taskIndex] = tasks[i];
        //}
        return EnqueueTaskResult.Success;
    }
    /// <summary>
    /// Tries to appends a set of tasks to the task queue. Does not acquire a lock; cannot return <see cref="EnqueueTaskResult.Contested"/>.
    /// </summary>
    /// <param name="tasks">Tasks composing the job.</param>
    /// <returns>Result of the enqueue attempt.</returns>
    /// <remarks>This must not be used while other threads could be performing task enqueues or task dequeues.</remarks>
    public EnqueueTaskResult TryEnqueueTasksUnsafely(Span<Task> tasks)
    {
        EnqueueTaskResult result;
        if ((result = TryEnqueueTasksUnsafelyInternal(tasks, out var taskEndIndex)) == EnqueueTaskResult.Success)
        {
            writtenTaskIndex = taskEndIndex;
        }
        return result;
    }

    /// <summary>
    /// Tries to appends a set of tasks to the task queue if the ring buffer is uncontested.
    /// </summary>
    /// <param name="tasks">Tasks composing the job.</param>
    /// <returns>Result of the enqueue attempt.</returns>
    public EnqueueTaskResult TryEnqueueTasks(Span<Task> tasks)
    {
        if (tasks.Length == 0)
            return EnqueueTaskResult.Success;
        if (Interlocked.CompareExchange(ref taskLocker, 1, 0) != 0)
            return EnqueueTaskResult.Contested;
        try
        {
            //We have the lock.
            EnqueueTaskResult result;
            if ((result = TryEnqueueTasksUnsafelyInternal(tasks, out var taskEndIndex)) == EnqueueTaskResult.Success)
            {
                if (Environment.Is64BitProcess)
                {
                    Volatile.Write(ref writtenTaskIndex, taskEndIndex);
                }
                else
                {
                    Interlocked.Exchange(ref writtenTaskIndex, taskEndIndex);
                }
            }
            return result;
        }
        finally
        {
            taskLocker = 0;
        }
    }

    /// <summary>
    /// Appends a set of tasks to the queue.
    /// </summary>
    /// <param name="tasks">Tasks composing the job.</param>
    /// <param name="workerIndex">Worker index to pass to inline-executed tasks if the task buffer is full.</param>
    /// <remarks>Note that this will keep trying until task submission succeeds. 
    /// If the task queue is full, this will opt to run some tasks inline while waiting for room.</remarks>
    public void EnqueueTasks(Span<Task> tasks, int workerIndex)
    {
        var waiter = new SpinWait();
        EnqueueTaskResult result;
        while ((result = TryEnqueueTasks(tasks)) != EnqueueTaskResult.Success)
        {
            if (result == EnqueueTaskResult.Full)
            {
                //Couldn't enqueue the tasks because the task buffer is full.
                //Clearly there's plenty of work available to execute, so go ahead and try to run one task inline.
                var task = tasks[0];
                task.Function(task.TaskId, task.Context, workerIndex);
                if (tasks.Length == 1)
                    break;
                tasks = tasks[1..];
            }
            else
            {
                waiter.SpinOnce(-1);
            }
        }
    }
    /// <summary>
    /// Tries to enqueues the stop command. 
    /// </summary>
    /// <returns>Result status of the enqueue attempt.</returns>
    public EnqueueTaskResult TryEnqueueStop()
    {
        Span<Task> stopJob = stackalloc Task[1];
        stopJob[0] = new Task { Function = null };
        return TryEnqueueTasks(stopJob);
    }

    /// <summary>
    /// Enqueues the stop command. 
    /// </summary>
    /// <param name="workerIndex">Worker index to pass to any inline executed tasks if the task buffer is full.</param>
    /// <remarks>Note that this will keep trying until task submission succeeds.
    /// If the task buffer is full, this may attempt to dequeue tasks to run inline.</remarks>
    public void EnqueueStop(int workerIndex)
    {
        Span<Task> stopJob = stackalloc Task[1];
        stopJob[0] = new Task { Function = null };
        var waiter = new SpinWait();
        EnqueueTaskResult result;
        while ((result = TryEnqueueTasks(stopJob)) != EnqueueTaskResult.Success)
        {
            if (result == EnqueueTaskResult.Full)
            {
                var dequeueResult = TryDequeueAndRun(workerIndex);
                Debug.Assert(dequeueResult != DequeueTaskResult.Stop, "We're trying to enqueue a stop, we shouldn't have found one already present!");
            }
            else
            {
                waiter.SpinOnce(-1);
            }
        }
    }

    /// <summary>
    /// Tries to enqueues the stop command. Does not take a lock; cannot return <see cref="EnqueueTaskResult.Contested"/>.
    /// </summary>
    /// <returns>Result status of the enqueue attempt.</returns>
    /// <remarks>This must not be used while other threads could be performing task enqueues or task dequeues.</remarks>
    public EnqueueTaskResult TryEnqueueStopUnsafely()
    {
        Span<Task> stopJob = stackalloc Task[1];
        stopJob[0] = new Task { Function = null };
        return TryEnqueueTasksUnsafely(stopJob);
    }

    /// <summary>
    /// Wraps a set of tasks in continuation tasks that will report their completion.
    /// </summary>
    /// <param name="continuationHandle">Handle of the continuation to report to.</param>
    /// <param name="tasks">Tasks to wrap.</param>
    /// <param name="wrappedTaskContexts">Contexts to be used for the wrapped tasks. This memory must persist until the wrapped tasks complete.</param>
    /// <param name="wrappedTasks">Span to hold the tasks created by this function.</param>
    public void CreateCompletionWrappedTasks(ContinuationHandle continuationHandle, Span<Task> tasks, WrappedTaskContext* wrappedTaskContexts, Span<Task> wrappedTasks)
    {
        var count = Math.Min(tasks.Length, wrappedTasks.Length);
        Debug.Assert(tasks.Length == wrappedTasks.Length, "This is probably a bug!");
        for (int i = 0; i < count; ++i)
        {
            ref var sourceTask = ref tasks[i];
            var wrappedContext = wrappedTaskContexts + i;
            ref var targetTask = ref wrappedTasks[i];
            wrappedContext->Function = sourceTask.Function;
            wrappedContext->Context = sourceTask.Context;
            wrappedContext->Continuation = continuationHandle;
            wrappedContext->Continuations = continuationsContainer.Memory;
            targetTask.Function = &RunAndMarkAsComplete;
            targetTask.Context = wrappedContext;
            targetTask.TaskId = sourceTask.TaskId;
        }
    }

    static void RunAndMarkAsComplete(int taskId, void* wrapperContextPointer, int workerIndex)
    {
        var wrapperContext = (WrappedTaskContext*)wrapperContextPointer;
        wrapperContext->Function(taskId, wrapperContext->Context, workerIndex);
        var continuationHandle = wrapperContext->Continuation;
        var continuations = wrapperContext->Continuations;
        var continuation = continuations->GetContinuation(continuationHandle);
        var counter = Interlocked.Decrement(ref continuation->RemainingTaskCounter);
        if (counter == 0)
        {
            //This entire job has completed.
            if (continuation->OnCompleted != null)
            {
                continuation->OnCompleted(continuation->UserId, continuation->OnCompletedContext, workerIndex);
            }
            //Free this continuation slot.
            var waiter = new SpinWait();
            while (true)
            {
                if (Interlocked.CompareExchange(ref continuations->Locker, 1, 0) != 0)
                {
                    waiter.SpinOnce(-1);
                }
                else
                {
                    //We have the lock.
                    continuations->IndexPool.ReturnUnsafely((int)continuationHandle.Index);
                    --continuations->ContinuationCount;
                    continuations->Locker = 0;
                    break;
                }
            }
        }
    }

    /// <summary>
    /// Attempts to allocate a continuation for a set of tasks.
    /// </summary>
    /// <param name="taskCount">Number of tasks associated with the continuation.</param>
    /// <param name="userContinuationId">User id to associate with the handle.</param>
    /// <param name="onCompleted">Function to execute upon completing all associated tasks.</param>
    /// <param name="onCompletedContext">Context pointer to pass into the completion function.</param>
    /// <param name="continuationHandle">Handle of the continuation if allocation is successful.</param>
    /// <returns>Result status of the continuation allocation attempt.</returns>
    public AllocateTaskContinuationResult TryAllocateContinuation(int taskCount, ulong userContinuationId, delegate*<ulong, void*, int, void> onCompleted, void* onCompletedContext, out ContinuationHandle continuationHandle)
    {
        continuationHandle = default;
        ref var continuations = ref continuationsContainer[0];
        if (Interlocked.CompareExchange(ref continuations.Locker, 1, 0) != 0)
            return AllocateTaskContinuationResult.Contested;
        try
        {
            //We have the lock.
            Debug.Assert(continuations.ContinuationCount <= continuations.Continuations.length);
            if (continuations.ContinuationCount >= continuations.Continuations.length)
            {
                //No room.
                return AllocateTaskContinuationResult.Full;
            }
            var index = continuations.IndexPool.Take();
            ref var continuation = ref continuations.Continuations[index];
            var newVersion = continuation.Version + 1;
            continuation.OnCompletedContext = onCompletedContext;
            continuation.OnCompleted = onCompleted;
            continuation.UserId = userContinuationId;
            continuation.Version = newVersion;
            continuation.RemainingTaskCounter = taskCount;
            continuationHandle = new ContinuationHandle((uint)index, newVersion);
            return AllocateTaskContinuationResult.Success;
        }
        finally
        {
            continuations.Locker = 0;
        }
    }

    /// <summary>
    /// Allocates a continuation for a set of tasks.
    /// </summary>
    /// <param name="taskCount">Number of tasks associated with the continuation.</param>
    /// <param name="workerIndex">Worker index to pass to any inline executed tasks if the continuations buffer is full.</param>
    /// <param name="userContinuationId">User id to associate with the handle.</param>
    /// <param name="onCompleted">Function to execute upon completing all associated tasks.</param>
    /// <param name="onCompletedContext">Context pointer to pass into the completion function.</param>
    /// <returns>Handle of the allocated continuation.</returns>
    /// <remarks>Note that this will keep trying until allocation succeeds. If something is blocking allocation, such as insufficient room in the continuations buffer and there are no workers consuming tasks, this will block forever.</remarks>
    public ContinuationHandle AllocateContinuation(int taskCount, int workerIndex, ulong userContinuationId = 0, delegate*<ulong, void*, int, void> onCompleted = null, void* onCompletedContext = null)
    {
        var waiter = new SpinWait();
        ContinuationHandle handle;
        AllocateTaskContinuationResult result;
        while ((result = TryAllocateContinuation(taskCount, userContinuationId, onCompleted, onCompletedContext, out handle)) != AllocateTaskContinuationResult.Success)
        {
            if (result == AllocateTaskContinuationResult.Full)
            {
                var dequeueResult = TryDequeueAndRun(workerIndex);
                Debug.Assert(dequeueResult != DequeueTaskResult.Stop, "We're trying to allocate a continuation, we shouldn't have run into a stop command!");
            }
            else
            {
                waiter.SpinOnce(-1);
            }
        }
        return handle;
    }

    /// <summary>
    /// Enqueues a for loop onto the task queue. Does not take a lock; cannot return <see cref="EnqueueTaskResult.Contested"/>.
    /// </summary>
    /// <param name="function">Function to execute on each iteration of the loop.</param>
    /// <param name="context">Context pointer to pass into each task execution.</param>
    /// <param name="inclusiveStartIndex">Inclusive start index of the loop range.</param>
    /// <param name="exclusiveEndIndex">Exclusive end index of the loop range.</param>
    /// <returns>Status result of the enqueue operation.</returns>
    /// <remarks>This must not be used while other threads could be performing task enqueues or task dequeues.</remarks>
    public EnqueueTaskResult TryEnqueueForUnsafely(delegate*<int, void*, int, void> function, void* context, int inclusiveStartIndex, int exclusiveEndIndex)
    {
        var taskCount = exclusiveEndIndex - inclusiveStartIndex;
        Span<Task> tasks = stackalloc Task[taskCount];
        for (int i = 0; i < tasks.Length; ++i)
        {
            tasks[i] = new Task { Function = function, Context = context, TaskId = i + inclusiveStartIndex };
        }
        return TryEnqueueTasksUnsafely(tasks);
    }

    /// <summary>
    /// Enqueues a for loop onto the task queue.
    /// </summary>
    /// <param name="function">Function to execute on each iteration of the loop.</param>
    /// <param name="context">Context pointer to pass into each task execution.</param>
    /// <param name="inclusiveStartIndex">Inclusive start index of the loop range.</param>
    /// <param name="exclusiveEndIndex">Exclusive end index of the loop range.</param>
    /// <param name="workerIndex">Worker index to pass to any inline-executed task if the task queue is full.</param>
    /// <remarks>This function will not usually attempt to run any iterations of the loop itself. It tries to push the loop tasks onto the queue.<para/>
    /// If the task queue is full, this will opt to run the tasks inline while waiting for room.</remarks>
    public void EnqueueFor(delegate*<int, void*, int, void> function, void* context, int inclusiveStartIndex, int exclusiveEndIndex, int workerIndex)
    {
        var taskCount = exclusiveEndIndex - inclusiveStartIndex;
        Span<Task> tasks = stackalloc Task[taskCount];
        for (int i = 0; i < tasks.Length; ++i)
        {
            tasks[i] = new Task { Function = function, Context = context, TaskId = i + inclusiveStartIndex };
        }
        EnqueueTasks(tasks, workerIndex);
    }

    /// <summary>
    /// Submits a set of tasks representing a for loop over the given indices and returns when all loop iterations are complete.
    /// </summary>
    /// <param name="function">Function to execute on each iteration of the loop.</param>
    /// <param name="context">Context pointer to pass into each iteration of the loop.</param>
    /// <param name="inclusiveStartIndex">Inclusive start index of the loop range.</param>
    /// <param name="exclusiveEndIndex">Exclusive end index of the loop range.</param>
    /// <param name="workerIndex">Index of the currently executing worker.</param>
    public void For(delegate*<int, void*, int, void> function, void* context, int inclusiveStartIndex, int exclusiveEndIndex, int workerIndex)
    {
        var iterationCount = exclusiveEndIndex - inclusiveStartIndex;
        if (iterationCount <= 0)
            return;
        ContinuationHandle continuationHandle = default;
        if (iterationCount > 1)
        {
            //Note that we only submit tasks to the queue for tasks beyond the first. The current thread is responsible for at least task 0.
            var taskCount = iterationCount - 1;
            WrappedTaskContext* wrappedContexts = stackalloc WrappedTaskContext[taskCount];
            Span<Task> tasks = stackalloc Task[taskCount];
            continuationHandle = AllocateContinuation(taskCount, workerIndex);
            for (int i = 0; i < tasks.Length; ++i)
            {
                var wrappedTaskContext = wrappedContexts + i;
                *wrappedTaskContext = new WrappedTaskContext { Function = function, Context = context, Continuation = continuationHandle, Continuations = continuationsContainer.Memory };
                tasks[i] = new Task { Function = &RunAndMarkAsComplete, Context = wrappedTaskContext, TaskId = i + 1 + inclusiveStartIndex };
            }
            var waiter = new SpinWait();
            EnqueueTaskResult result;
            while ((result = TryEnqueueTasks(tasks)) != EnqueueTaskResult.Success)
            {
                if (result == EnqueueTaskResult.Full)
                {
                    //If the task buffer is full, just execute the task locally. Clearly there's enough work for other threads to keep running productively.
                    var task = tasks[0];
                    task.Function(task.TaskId, task.Context, workerIndex);
                    if (tasks.Length == 1)
                        break;
                    tasks = tasks[1..];
                }
                else
                {
                    waiter.SpinOnce(-1); //TODO: We're biting the bullet on yields/sleep(0) here. May not be ideal for the use case; investigate
                }
            }
        }
        //Tasks [1, count) are submitted to the queue and may now be executing on other workers.
        //The thread calling the for loop should not relinquish its timeslice. It should immediately begin working on task 0.
        function(inclusiveStartIndex, context, workerIndex);

        if (iterationCount > 1)
        {
            //Task 0 is done; this thread should seek out other work until the job is complete.
            var waiter = new SpinWait();
            while (!IsComplete(continuationHandle))
            {
                //Note that we don't handle the DequeueResult.Stop case; if the job isn't complete yet, there's no way to hit a stop unless we enqueued this job after a stop.
                //Enqueuing after a stop is an error condition and is debug checked for in TryEnqueueJob.
                var dequeueResult = TryDequeue(out var fillerJob, out var fillerContext, out var fillerTaskId);
                if (dequeueResult == DequeueTaskResult.Stop)
                {
                    Debug.Assert(dequeueResult != DequeueTaskResult.Stop, "Did you enqueue this for loop *after* some thread enqueued a stop command? That's illegal!");
                    return;
                }
                if (dequeueResult == DequeueTaskResult.Success)
                {
                    fillerJob(fillerTaskId, fillerContext, workerIndex);
                    waiter.Reset();
                }
                else
                {
                    waiter.SpinOnce(-1);
                }
            }
        }
    }
}
