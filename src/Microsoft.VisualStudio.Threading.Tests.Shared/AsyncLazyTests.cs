﻿/********************************************************
*                                                        *
*   © Copyright (C) Microsoft. All rights reserved.      *
*                                                        *
*********************************************************/

namespace Microsoft.VisualStudio.Threading.Tests
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using System.Windows.Threading;
    using Xunit;
    using GenericParameterHelper = Microsoft.VisualStudio.Threading.Tests.Shared.GenericParameterHelper;

    public class AsyncLazyTests : TestBase
    {
        [Fact]
        public async Task Basic()
        {
            var expected = new GenericParameterHelper(5);
            var lazy = new AsyncLazy<GenericParameterHelper>(async delegate
            {
                await Task.Yield();
                return expected;
            });

            var actual = await lazy.GetValueAsync();
            Assert.Same(expected, actual);
        }

        [Fact]
        public async Task IsValueCreated()
        {
            var evt = new AsyncManualResetEvent();
            var lazy = new AsyncLazy<GenericParameterHelper>(async delegate
            {
                // Wait here, so we can verify that IsValueCreated is true
                // before the value factory has completed execution.
                await evt;
                return new GenericParameterHelper(5);
            });

            Assert.False(lazy.IsValueCreated);
            var resultTask = lazy.GetValueAsync();
            Assert.True(lazy.IsValueCreated);
            evt.Set();
            Assert.Equal(5, (await resultTask).Data);
            Assert.True(lazy.IsValueCreated);
        }

        [Fact]
        public async Task IsValueFactoryCompleted()
        {
            var evt = new AsyncManualResetEvent();
            var lazy = new AsyncLazy<GenericParameterHelper>(async delegate
            {
                await evt;
                return new GenericParameterHelper(5);
            });

            Assert.False(lazy.IsValueFactoryCompleted);
            var resultTask = lazy.GetValueAsync();
            Assert.False(lazy.IsValueFactoryCompleted);
            evt.Set();
            Assert.Equal(5, (await resultTask).Data);
            Assert.True(lazy.IsValueFactoryCompleted);
        }

        [Fact]
        public void CtorNullArgs()
        {
            Assert.Throws<ArgumentNullException>(() => new AsyncLazy<object>(null));
        }

        /// <summary>
        /// Verifies that multiple sequential calls to <see cref="AsyncLazy{T}.GetValueAsync"/>
        /// do not result in multiple invocations of the value factory.
        /// </summary>
        [Fact]
        public async Task ValueFactoryExecutedOnlyOnceSequential()
        {
            bool valueFactoryExecuted = false;
            var lazy = new AsyncLazy<GenericParameterHelper>(async delegate
            {
                Assert.False(valueFactoryExecuted);
                valueFactoryExecuted = true;
                await Task.Yield();
                return new GenericParameterHelper(5);
            });

            var task1 = lazy.GetValueAsync();
            var task2 = lazy.GetValueAsync();
            var actual1 = await task1;
            var actual2 = await task2;
            Assert.Same(actual1, actual2);
            Assert.Equal(5, actual1.Data);
        }

        /// <summary>
        /// Verifies that multiple concurrent calls to <see cref="AsyncLazy{T}.GetValueAsync"/>
        /// do not result in multiple invocations of the value factory.
        /// </summary>
        [Fact]
        public void ValueFactoryExecutedOnlyOnceConcurrent()
        {
            var cts = new CancellationTokenSource(AsyncDelay);
            while (!cts.Token.IsCancellationRequested)
            {
                bool valueFactoryExecuted = false;
                var lazy = new AsyncLazy<GenericParameterHelper>(async delegate
                {
                    Assert.False(valueFactoryExecuted);
                    valueFactoryExecuted = true;
                    await Task.Yield();
                    return new GenericParameterHelper(5);
                });

                var results = TestUtilities.ConcurrencyTest(delegate
                {
                    return lazy.GetValueAsync().Result;
                });

                Assert.Equal(5, results[0].Data);
                for (int i = 1; i < results.Length; i++)
                {
                    Assert.Same(results[0], results[i]);
                }
            }
        }

        [Fact]
        public async Task ValueFactoryReleasedAfterExecution()
        {
            for (int i = 0; i < 10; i++)
            {
                Console.WriteLine("Iteration {0}", i);
                WeakReference collectible = null;
                AsyncLazy<object> lazy = null;
                ((Action)(() =>
                {
                    var closure = new { value = new object() };
                    collectible = new WeakReference(closure);
                    lazy = new AsyncLazy<object>(async delegate
                    {
                        await Task.Yield();
                        return closure.value;
                    });
                }))();

                Assert.True(collectible.IsAlive);
                var result = await lazy.GetValueAsync();

                for (int j = 0; j < 3 && collectible.IsAlive; j++)
                {
                    GC.Collect(2, GCCollectionMode.Forced, true);
                    await Task.Yield();
                }

                // It turns out that the GC isn't predictable.  But as long as
                // we can get an iteration where the value has been GC'd, we can
                // be confident that the product is releasing the reference.
                if (!collectible.IsAlive)
                {
                    return; // PASS.
                }
            }

            Assert.True(false, "The reference was never released");
        }

        [Fact, Trait("TestCategory", "FailsInCloudTest")]
        public async Task AsyncPumpReleasedAfterExecution()
        {
            WeakReference collectible = null;
            AsyncLazy<object> lazy = null;
            ((Action)(() =>
            {
                var context = new JoinableTaskContext();
                collectible = new WeakReference(context.Factory);
                lazy = new AsyncLazy<object>(
                    async delegate
                    {
                        await Task.Yield();
                        return new object();
                    },
                    context.Factory);
            }))();

            Assert.True(collectible.IsAlive);
            var result = await lazy.GetValueAsync();

            var cts = new CancellationTokenSource(AsyncDelay);
            while (!cts.IsCancellationRequested && collectible.IsAlive)
            {
                await Task.Yield();
                GC.Collect();
            }

            Assert.False(collectible.IsAlive);
        }

        [Fact]
        public void ValueFactoryThrowsSynchronously()
        {
            bool executed = false;
            var lazy = new AsyncLazy<object>(new Func<Task<object>>(delegate
            {
                Assert.False(executed);
                executed = true;
                throw new ApplicationException();
            }));

            var task1 = lazy.GetValueAsync();
            var task2 = lazy.GetValueAsync();
            Assert.Same(task1, task2);
            Assert.True(task1.IsFaulted);
            Assert.IsType(typeof(ApplicationException), task1.Exception.InnerException);
        }

        [Fact]
        public async Task ValueFactoryReentersValueFactorySynchronously()
        {
            AsyncLazy<object> lazy = null;
            bool executed = false;
            lazy = new AsyncLazy<object>(delegate
            {
                Assert.False(executed);
                executed = true;
                lazy.GetValueAsync();
                return Task.FromResult<object>(new object());
            });

            await Assert.ThrowsAsync<InvalidOperationException>(() => lazy.GetValueAsync());

            // Do it again, to verify that AsyncLazy recorded the failure and will replay it.
            await Assert.ThrowsAsync<InvalidOperationException>(() => lazy.GetValueAsync());
        }

        [Fact]
        public async Task ValueFactoryReentersValueFactoryAsynchronously()
        {
            AsyncLazy<object> lazy = null;
            bool executed = false;
            lazy = new AsyncLazy<object>(async delegate
            {
                Assert.False(executed);
                executed = true;
                await Task.Yield();
                await lazy.GetValueAsync();
                return new object();
            });

            await Assert.ThrowsAsync<InvalidOperationException>(() => lazy.GetValueAsync());

            // Do it again, to verify that AsyncLazy recorded the failure and will replay it.
            await Assert.ThrowsAsync<InvalidOperationException>(() => lazy.GetValueAsync());
        }

        [Fact]
        public async Task GetValueAsyncWithCancellationToken()
        {
            var evt = new AsyncManualResetEvent();
            var lazy = new AsyncLazy<GenericParameterHelper>(async delegate
            {
                await evt;
                return new GenericParameterHelper(5);
            });

            var cts = new CancellationTokenSource();
            var task1 = lazy.GetValueAsync(cts.Token);
            var task2 = lazy.GetValueAsync();
            cts.Cancel();

            // Verify that the task returned from the canceled request actually completes before the value factory does.
            await Assert.ThrowsAsync<OperationCanceledException>(() => task1);

            // Now verify that the value factory does actually complete anyway for other callers.
            evt.Set();
            var task2Result = await task2;
            Assert.Equal(5, task2Result.Data);
        }

        [Fact]
        public async Task GetValueAsyncWithCancellationTokenPreCanceled()
        {
            var lazy = new AsyncLazy<GenericParameterHelper>(() => Task.FromResult(new GenericParameterHelper(5)));
            var cts = new CancellationTokenSource();
            cts.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => lazy.GetValueAsync(cts.Token));

            Assert.False(lazy.IsValueCreated, "Value factory should not have been invoked for a pre-canceled token.");
        }

        [Fact]
        public async Task GetValueAsyncAlreadyCompletedWithCancellationTokenPreCanceled()
        {
            var lazy = new AsyncLazy<GenericParameterHelper>(() => Task.FromResult(new GenericParameterHelper(5)));
            await lazy.GetValueAsync();

            var cts = new CancellationTokenSource();
            cts.Cancel();
            var result = await lazy.GetValueAsync(cts.Token); // this shouldn't throw canceled because it was already done.
            Assert.Equal(5, result.Data);
            Assert.True(lazy.IsValueCreated);
            Assert.True(lazy.IsValueFactoryCompleted);
        }

        [Fact]
        public void ToStringForUncreatedValue()
        {
            var lazy = new AsyncLazy<object>(() => Task.FromResult<object>(null));
            string result = lazy.ToString();
            Assert.NotNull(result);
            Assert.NotEqual(string.Empty, result);
            Assert.False(lazy.IsValueCreated);
        }

        [Fact]
        public async Task ToStringForCreatedValue()
        {
            var lazy = new AsyncLazy<int>(() => Task.FromResult<int>(3));
            var value = await lazy.GetValueAsync();
            string result = lazy.ToString();
            Assert.Equal(value.ToString(), result);
        }

        [Fact]
        public void ToStringForFaultedValue()
        {
            var lazy = new AsyncLazy<int>(delegate
            {
                throw new ApplicationException();
            });
            lazy.GetValueAsync().Forget();
            Assert.True(lazy.IsValueCreated);
            string result = lazy.ToString();
            Assert.NotNull(result);
            Assert.NotEqual(string.Empty, result);
        }

        /// <summary>
        /// Verifies that even after the value factory has been invoked
        /// its dependency on the Main thread can be satisfied by
        /// someone synchronously blocking on the Main thread that is
        /// also interested in its value.
        /// </summary>
        [Fact]
        public void ValueFactoryRequiresMainThreadHeldByOther()
        {
            var ctxt = new DispatcherSynchronizationContext();
            SynchronizationContext.SetSynchronizationContext(ctxt);
            var context = new JoinableTaskContext();
            var asyncPump = context.Factory;
            var originalThread = Thread.CurrentThread;

            var evt = new AsyncManualResetEvent();
            var lazy = new AsyncLazy<object>(
                async delegate
                {
                    await evt; // use an event here to ensure it won't resume till the Main thread is blocked.
                    return new object();
                },
                asyncPump);

            var resultTask = lazy.GetValueAsync();
            Assert.False(resultTask.IsCompleted);

            var collection = context.CreateCollection();
            var someRandomPump = context.CreateFactory(collection);
            someRandomPump.Run(async delegate
            {
                evt.Set(); // setting this event allows the value factory to resume, once it can get the Main thread.

                // The interesting bit we're testing here is that
                // the value factory has already been invoked.  It cannot
                // complete until the Main thread is available and we're blocking
                // the Main thread waiting for it to complete.
                // This will deadlock unless the AsyncLazy joins
                // the value factory's async pump with the currently blocking one.
                var value = await lazy.GetValueAsync();
                Assert.NotNull(value);
            });

            // Now that the value factory has completed, the earlier acquired
            // task should have no problem completing.
            Assert.True(resultTask.Wait(AsyncDelay));
        }

        [Fact(Skip = "Hangs. This test documents a deadlock scenario that is not fixed (by design, IIRC).")]
        public async Task ValueFactoryRequiresReadLockHeldByOther()
        {
            var lck = new AsyncReaderWriterLock();
            var readLockAcquiredByOther = new AsyncManualResetEvent();
            var writeLockWaitingByOther = new AsyncManualResetEvent();

            var lazy = new AsyncLazy<object>(
                async delegate
                {
                    await writeLockWaitingByOther;
                    using (await lck.ReadLockAsync())
                    {
                        return new object();
                    }
                });

            var writeLockTask = Task.Run(async delegate
            {
                await readLockAcquiredByOther;
                var writeAwaiter = lck.WriteLockAsync().GetAwaiter();
                writeAwaiter.OnCompleted(delegate
                {
                    using (writeAwaiter.GetResult())
                    {
                    }
                });
                writeLockWaitingByOther.Set();
            });

            // Kick off the value factory without any lock context.
            var resultTask = lazy.GetValueAsync();

            using (await lck.ReadLockAsync())
            {
                readLockAcquiredByOther.Set();

                // Now request the lazy task again.
                // This would traditionally deadlock because the value factory won't
                // be able to get its read lock while a write lock is waiting (for us to release ours).
                // This unit test verifies that the AsyncLazy<T> class can avoid deadlocks in this case.
                await lazy.GetValueAsync();
            }
        }
    }
}
