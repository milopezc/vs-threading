﻿namespace Microsoft.VisualStudio.Threading.Tests
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Linq;
    using System.Reflection;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using System.Windows.Threading;
    using System.Xml.Linq;
    using Xunit;
    using Xunit.Abstractions;
    using GenericParameterHelper = Shared.GenericParameterHelper;

    public class JoinableTaskTests : JoinableTaskTestBase
    {
        public JoinableTaskTests(ITestOutputHelper logger)
            : base(logger)
        {
        }

        [StaFact]
        public void RunFuncOfTaskSTA()
        {
            this.RunFuncOfTaskHelper();
        }

        [StaFact]
        public void RunFuncOfTaskMTA()
        {
            Task.Run(() => this.RunFuncOfTaskHelper()).Wait();
        }

        [StaFact]
        public void RunFuncOfTaskOfTSTA()
        {
            this.RunFuncOfTaskOfTHelper();
        }

        [StaFact]
        public void RunFuncOfTaskOfTMTA()
        {
            Task.Run(() => this.RunFuncOfTaskOfTHelper()).GetAwaiter().GetResult();
        }

        [StaFact]
        public void LeaveAndReturnToSTA()
        {
            var fullyCompleted = false;
            this.asyncPump.Run(async delegate
            {
                Assert.Same(this.originalThread, Thread.CurrentThread);

                await TaskScheduler.Default;
                Assert.NotSame(this.originalThread, Thread.CurrentThread);

                await this.asyncPump.SwitchToMainThreadAsync();
                Assert.Same(this.originalThread, Thread.CurrentThread);
                fullyCompleted = true;
            });
            Assert.True(fullyCompleted);
        }

        [StaFact]
        public void SwitchToMainThreadDoesNotYieldWhenAlreadyOnMainThread()
        {
            Assert.True(this.asyncPump.SwitchToMainThreadAsync().GetAwaiter().IsCompleted, "Yield occurred even when already on UI thread.");
        }

        [StaFact]
        public void SwitchToMainThreadYieldsWhenOffMainThread()
        {
            Task.Run(
                () => Assert.False(this.asyncPump.SwitchToMainThreadAsync().GetAwaiter().IsCompleted, "Yield did not occur when off Main thread."))
                .GetAwaiter().GetResult();
        }

        [StaFact]
        public void SwitchToMainThreadAsyncContributesToHangReportsAndCollections()
        {
            var mainThreadRequestPended = new ManualResetEventSlim();
            var frame = new DispatcherFrame();
            Exception delegateFailure = null;

            Task.Run(delegate
            {
                var awaiter = this.asyncPump.SwitchToMainThreadAsync().GetAwaiter();
                awaiter.OnCompleted(delegate
                {
                    try
                    {
                        Assert.Same(this.originalThread, Thread.CurrentThread);
                    }
                    catch (Exception ex)
                    {
                        delegateFailure = ex;
                    }
                    finally
                    {
                        frame.Continue = false;
                    }
                });
                mainThreadRequestPended.Set();
            });

            Assert.True(mainThreadRequestPended.Wait(TestTimeout));

            // Verify here that pendingTasks includes one task.
            Assert.Equal(1, this.GetPendingTasksCount());
            Assert.Equal(1, this.joinableCollection.Count());

            // Now let the request proceed through.
            Dispatcher.PushFrame(frame);

            Assert.Equal(0, this.GetPendingTasksCount());
            Assert.Equal(0, this.joinableCollection.Count());

            if (delegateFailure != null)
            {
                throw new TargetInvocationException(delegateFailure);
            }
        }

        [StaFact]
        public void SwitchToMainThreadAsyncWithinCompleteTaskGetsNewTask()
        {
            // For this test, the JoinableTaskFactory we use shouldn't have its own collection.
            // This is important for hitting the code path that was buggy before this test was written.
            this.joinableCollection = null;
            this.asyncPump = new DerivedJoinableTaskFactory(this.context);

            var frame = new DispatcherFrame();
            var outerTaskCompleted = new AsyncManualResetEvent();
            Task innerTask = null;
            this.asyncPump.RunAsync(delegate
            {
                innerTask = Task.Run(async delegate
                {
                    await outerTaskCompleted;

                    // This thread transition runs within the context of a completed task.
                    // In this transition, the JoinableTaskFactory should create a new, incompleted
                    // task to represent the transition.
                    // This is verified by our DerivedJoinableTaskFactory which will throw if
                    // the task has already completed.
                    await this.asyncPump.SwitchToMainThreadAsync();
                });

                return TplExtensions.CompletedTask;
            });
            outerTaskCompleted.Set();

            innerTask.ContinueWith(_ => frame.Continue = false);

            // Now let the request proceed through.
            Dispatcher.PushFrame(frame);

            innerTask.Wait(); // rethrow exceptions
        }

        [StaFact]
        public void SwitchToMainThreadAsyncTwiceRemainsInJoinableCollection()
        {
            ((DerivedJoinableTaskFactory)this.asyncPump).AssumeConcurrentUse = true;
            var mainThreadRequestPended = new ManualResetEventSlim();
            var frame = new DispatcherFrame();
            Exception delegateFailure = null;

            Task.Run(delegate
            {
                this.asyncPump.RunAsync(
                    delegate
                    {
                        var awaiter = this.asyncPump.SwitchToMainThreadAsync().GetAwaiter();
                        awaiter.OnCompleted(
                            delegate
                            {
                                try
                                {
                                    Assert.Same(this.originalThread, Thread.CurrentThread);
                                }
                                catch (Exception ex)
                                {
                                    delegateFailure = ex;
                                }
                                finally
                                {
                                    frame.Continue = false;
                                }
                            });
                        awaiter.OnCompleted(
                            delegate
                            {
                                try
                                {
                                    Assert.Same(this.originalThread, Thread.CurrentThread);
                                }
                                catch (Exception ex)
                                {
                                    delegateFailure = ex;
                                }
                                finally
                                {
                                    frame.Continue = false;
                                }
                            });
                        return TplExtensions.CompletedTask;
                    });
                mainThreadRequestPended.Set();
            });

            Assert.True(mainThreadRequestPended.Wait(TestTimeout));

            // Verify here that pendingTasks includes one task.
            Assert.Equal(1, ((DerivedJoinableTaskFactory)this.asyncPump).TransitioningTasksCount);

            // Now let the request proceed through.
            Dispatcher.PushFrame(frame);
            frame.Continue = true; // reset for next time

            // Verify here that pendingTasks includes one task.
            Assert.Equal(1, ((DerivedJoinableTaskFactory)this.asyncPump).TransitioningTasksCount);

            // Now let the request proceed through.
            Dispatcher.PushFrame(frame);

            Assert.Equal(0, ((DerivedJoinableTaskFactory)this.asyncPump).TransitioningTasksCount);

            if (delegateFailure != null)
            {
                throw new TargetInvocationException(delegateFailure);
            }
        }

        [StaFact]
        public void SwitchToMainThreadAsyncTransitionsCanSeeAsyncLocals()
        {
            var mainThreadRequestPended = new ManualResetEventSlim();
            var frame = new DispatcherFrame();
            Exception delegateFailure = null;

            var asyncLocal = new AsyncLocal<object>();
            var asyncLocalValue = new object();

            // The point of this test is to verify that the transitioning/transitioned
            // methods on the JoinableTaskFactory can see into the AsyncLocal<T>.Value
            // as defined in the context that is requesting the transition.
            // The ProjectLockService depends on this behavior to identify UI thread
            // requestors that hold a project lock, on both sides of the transition.
            ((DerivedJoinableTaskFactory)this.asyncPump).TransitioningToMainThreadCallback =
                jt => { Assert.Same(asyncLocalValue, asyncLocal.Value); };
            ((DerivedJoinableTaskFactory)this.asyncPump).TransitionedToMainThreadCallback =
                jt => { Assert.Same(asyncLocalValue, asyncLocal.Value); };

            Task.Run(delegate
            {
                asyncLocal.Value = asyncLocalValue;
                var awaiter = this.asyncPump.SwitchToMainThreadAsync().GetAwaiter();
                awaiter.OnCompleted(delegate
                {
                    try
                    {
                        Assert.Same(this.originalThread, Thread.CurrentThread);
                        Assert.Same(asyncLocalValue, asyncLocal.Value);
                    }
                    catch (Exception ex)
                    {
                        delegateFailure = ex;
                    }
                    finally
                    {
                        frame.Continue = false;
                    }
                });
                mainThreadRequestPended.Set();
            });

            Assert.True(mainThreadRequestPended.Wait(TestTimeout));

            // Now let the request proceed through.
            Dispatcher.PushFrame(frame);

            if (delegateFailure != null)
            {
                throw new TargetInvocationException(delegateFailure);
            }
        }

        [StaFact]
        public void SwitchToMainThreadCancellable()
        {
            var task = Task.Run(async delegate
            {
                var cts = new CancellationTokenSource(AsyncDelay);
                try
                {
                    await this.asyncPump.SwitchToMainThreadAsync(cts.Token);
                    Assert.True(false, "Expected OperationCanceledException not thrown.");
                }
                catch (OperationCanceledException)
                {
                }

                Assert.NotSame(this.originalThread, Thread.CurrentThread);
            });

            Assert.True(task.Wait(TestTimeout), "Test timed out.");
        }

        [StaFact]
        public void SwitchToMainThreadCancellableWithinRun()
        {
            var endTestTokenSource = new CancellationTokenSource(AsyncDelay);
            try
            {
                this.asyncPump.Run(delegate
                {
                    using (this.context.SuppressRelevance())
                    {
                        return Task.Run(async delegate
                        {
                            await this.asyncPump.SwitchToMainThreadAsync(endTestTokenSource.Token);
                        });
                    }
                });
                Assert.True(false, "Expected OperationCanceledException not thrown.");
            }
            catch (OperationCanceledException)
            {
            }
        }

        /// <summary>
        /// Verify that if the <see cref="JoinableTaskContext"/> was initialized
        /// without a <see cref="SynchronizationContext"/> whose
        /// <see cref="SynchronizationContext.Post"/> method executes its delegate
        /// on the <see cref="Thread"/> passed to the <see cref="JoinableTaskContext"/>
        /// constructor, that an attempt to switch to the main thread using JTF
        /// throws an informative exception.
        /// </summary>
        [StaFact]
        public async Task SwitchToMainThreadThrowsUsefulExceptionIfJTCIsMisconfigured()
        {
            SynchronizationContext.SetSynchronizationContext(new SynchronizationContext());
            var jtc = new JoinableTaskContext();
            await TaskScheduler.Default;
            try
            {
                await jtc.Factory.SwitchToMainThreadAsync();
            }
            catch (JoinableTaskContextException)
            {
            }
        }

        [StaFact]
        public void SwitchToSTADoesNotCauseUnrelatedReentrancy()
        {
            var frame = new DispatcherFrame();

            var uiThreadNowBusy = new TaskCompletionSource<object>();
            bool contenderHasReachedUIThread = false;

            var backgroundContender = Task.Run(async delegate
            {
                await uiThreadNowBusy.Task;
                await this.asyncPump.SwitchToMainThreadAsync();
                Assert.Same(this.originalThread, Thread.CurrentThread);
                contenderHasReachedUIThread = true;
                frame.Continue = false;
            });

            this.asyncPump.Run(async delegate
            {
                uiThreadNowBusy.SetResult(null);
                Assert.Same(this.originalThread, Thread.CurrentThread);

                await TaskScheduler.Default;
                Assert.NotSame(this.originalThread, Thread.CurrentThread);
                await Task.Delay(AsyncDelay); // allow ample time for the background contender to re-enter the STA thread if it's possible (we don't want it to be).

                await this.asyncPump.SwitchToMainThreadAsync();
                Assert.Same(this.originalThread, Thread.CurrentThread);
                Assert.False(contenderHasReachedUIThread, "The contender managed to get to the STA thread while other work was on it.");
            });

            // Pump messages until everything's done.
            Dispatcher.PushFrame(frame);

            Assert.True(backgroundContender.Wait(AsyncDelay), "Background contender never reached the UI thread.");
        }

        [StaFact]
        public void SwitchToSTASucceedsForRelevantWork()
        {
            this.asyncPump.Run(async delegate
            {
                var backgroundContender = Task.Run(async delegate
                {
                    await this.asyncPump.SwitchToMainThreadAsync();
                    Assert.Same(this.originalThread, Thread.CurrentThread);
                });

                Assert.Same(this.originalThread, Thread.CurrentThread);

                await TaskScheduler.Default;
                Assert.NotSame(this.originalThread, Thread.CurrentThread);

                // We can't complete until this seemingly unrelated work completes.
                // This shouldn't deadlock because this synchronous operation kicked off
                // the operation to begin with.
                await backgroundContender;

                await this.asyncPump.SwitchToMainThreadAsync();
                Assert.Same(this.originalThread, Thread.CurrentThread);
            });
        }

        [StaFact]
        public void SwitchToSTASucceedsForDependentWork()
        {
            var uiThreadNowBusy = new TaskCompletionSource<object>();
            var backgroundContenderCompletedRelevantUIWork = new TaskCompletionSource<object>();
            var backgroundInvitationReverted = new TaskCompletionSource<object>();
            bool syncUIOperationCompleted = false;

            var backgroundContender = Task.Run(async delegate
            {
                await uiThreadNowBusy.Task;
                await this.asyncPump.SwitchToMainThreadAsync();
                Assert.Same(this.originalThread, Thread.CurrentThread);

                // Release, then reacquire the STA a couple of different ways
                // to verify that even after the invitation has been extended
                // to join the STA thread we can leave and revisit.
                await this.asyncPump.SwitchToMainThreadAsync();
                Assert.Same(this.originalThread, Thread.CurrentThread);
                await Task.Yield();
                Assert.Same(this.originalThread, Thread.CurrentThread);

                // Now complete the task that the synchronous work is waiting before reverting their invitation.
                backgroundContenderCompletedRelevantUIWork.SetResult(null);

                // Temporarily get off UI thread until the UI thread has rescinded offer to lend its time.
                // In so doing, once the task we're waiting on has completed, we'll be scheduled to return using
                // the current synchronization context, which because we switched to the main thread earlier
                // and have not yet switched off, will mean our continuation won't execute until the UI thread
                // becomes available (without any reentrancy).
                await backgroundInvitationReverted.Task;

                // We should now be on the UI thread (and the Run delegate below should have altogether completd.)
                Assert.Same(this.originalThread, Thread.CurrentThread);
                Assert.True(syncUIOperationCompleted); // should be true because continuation needs same thread that this is set on.
            });

            this.asyncPump.Run(async delegate
            {
                uiThreadNowBusy.SetResult(null);
                Assert.Same(this.originalThread, Thread.CurrentThread);

                await TaskScheduler.Default;
                Assert.NotSame(this.originalThread, Thread.CurrentThread);

                using (this.joinableCollection.Join())
                { // invite the work to re-enter our synchronous work on the STA thread.
                    await backgroundContenderCompletedRelevantUIWork.Task; // we can't complete until this seemingly unrelated work completes.
                } // stop inviting more work from background thread.

                await this.asyncPump.SwitchToMainThreadAsync();
                var nowait = backgroundInvitationReverted.SetAsync();
                Assert.Same(this.originalThread, Thread.CurrentThread);
                syncUIOperationCompleted = true;

                using (this.joinableCollection.Join())
                {
                    // Since this background task finishes on the UI thread, we need to ensure
                    // it can get on it.
                    await backgroundContender;
                }
            });
        }

        [StaFact]
        public void TransitionToMainThreadNotRaisedWhenAlreadyOnMainThread()
        {
            var factory = (DerivedJoinableTaskFactory)this.asyncPump;

            factory.Run(async delegate
            {
                // Switch to main thread when we're already there.
                await factory.SwitchToMainThreadAsync();
                Assert.Equal(0, factory.TransitioningToMainThreadHitCount); //, "No transition expected since we're already on the main thread.");
                Assert.Equal(0, factory.TransitionedToMainThreadHitCount); //, "No transition expected since we're already on the main thread.");

                // While on the main thread, await something that executes on a background thread.
                await Task.Run(delegate
            {
                Assert.Equal(0, factory.TransitioningToMainThreadHitCount); //, "No transition expected when moving off the main thread.");
                Assert.Equal(0, factory.TransitionedToMainThreadHitCount); //, "No transition expected when moving off the main thread.");
            });
                Assert.Equal(0, factory.TransitioningToMainThreadHitCount); //, "No transition expected since the main thread was ultimately blocked for this job.");
                Assert.Equal(0, factory.TransitionedToMainThreadHitCount); //, "No transition expected since the main thread was ultimately blocked for this job.");

                // Now switch explicitly to a threadpool thread.
                await TaskScheduler.Default;
                Assert.Equal(0, factory.TransitioningToMainThreadHitCount); //, "No transition expected when moving off the main thread.");
                Assert.Equal(0, factory.TransitionedToMainThreadHitCount); //, "No transition expected when moving off the main thread.");

                // Now switch back to the main thread.
                await factory.SwitchToMainThreadAsync();
                Assert.Equal(0, factory.TransitioningToMainThreadHitCount); //, "No transition expected because the main thread was ultimately blocked for this job.");
                Assert.Equal(0, factory.TransitionedToMainThreadHitCount); //, "No transition expected because the main thread was ultimately blocked for this job.");
            });
        }

        [StaFact]
        public void TransitionToMainThreadRaisedWhenSwitchingToMainThread()
        {
            var factory = (DerivedJoinableTaskFactory)this.asyncPump;

            var joinableTask = factory.RunAsync(async delegate
            {
                // Switch to main thread when we're already there.
                await factory.SwitchToMainThreadAsync();
                Assert.Equal(0, factory.TransitioningToMainThreadHitCount); //, "No transition expected since we're already on the main thread.");
                Assert.Equal(0, factory.TransitionedToMainThreadHitCount); //, "No transition expected since we're already on the main thread.");

                // While on the main thread, await something that executes on a background thread.
                await Task.Run(delegate
                {
                    Assert.Equal(0, factory.TransitioningToMainThreadHitCount); //, "No transition expected when moving off the main thread.");
                    Assert.Equal(0, factory.TransitionedToMainThreadHitCount); //, "No transition expected when moving off the main thread.");
                });
                Assert.Equal(1, factory.TransitioningToMainThreadHitCount); //, "Reacquisition of main thread should have raised transition events.");
                Assert.Equal(1, factory.TransitionedToMainThreadHitCount); //, "Reacquisition of main thread should have raised transition events.");

                // Now switch explicitly to a threadpool thread.
                await TaskScheduler.Default;
                Assert.Equal(1, factory.TransitioningToMainThreadHitCount); // No transition expected when moving off the main thread.
                Assert.Equal(1, factory.TransitionedToMainThreadHitCount); // No transition expected when moving off the main thread.

                // Now switch back to the main thread.
                await factory.SwitchToMainThreadAsync();
                Assert.Equal(2, factory.TransitioningToMainThreadHitCount); // Reacquisition of main thread should have raised transition events.
                Assert.Equal(2, factory.TransitionedToMainThreadHitCount); // Reacquisition of main thread should have raised transition events.
            });

            // Simulate the UI thread just pumping ordinary messages
            var frame = new DispatcherFrame();
            joinableTask.Task.ContinueWith(_ => frame.Continue = false, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            Dispatcher.PushFrame(frame);
            joinableTask.Join(); // Throw exceptions thrown by the async task.
        }

        [StaFact]
        public void RunSynchronouslyNestedNoJoins()
        {
            bool outerCompleted = false, innerCompleted = false;
            this.asyncPump.Run(async delegate
            {
                Assert.Same(this.originalThread, Thread.CurrentThread);
                await Task.Yield();
                Assert.Same(this.originalThread, Thread.CurrentThread);

                await Task.Run(async delegate
                {
                    await this.asyncPump.SwitchToMainThreadAsync();
                    Assert.Same(this.originalThread, Thread.CurrentThread);
                });

                this.asyncPump.Run(async delegate
                {
                    Assert.Same(this.originalThread, Thread.CurrentThread);
                    await Task.Yield();
                    Assert.Same(this.originalThread, Thread.CurrentThread);

                    await Task.Run(async delegate
                    {
                        await this.asyncPump.SwitchToMainThreadAsync();
                        Assert.Same(this.originalThread, Thread.CurrentThread);
                    });

                    Assert.Same(this.originalThread, Thread.CurrentThread);
                    innerCompleted = true;
                });

                await Task.Yield();
                Assert.Same(this.originalThread, Thread.CurrentThread);
                outerCompleted = true;
            });

            Assert.True(innerCompleted, "Nested Run did not complete.");
            Assert.True(outerCompleted, "Outer Run did not complete.");
        }

        [StaFact]
        public void RunSynchronouslyNestedWithJoins()
        {
            bool outerCompleted = false, innerCompleted = false;

            this.asyncPump.Run(async delegate
            {
                Assert.Same(this.originalThread, Thread.CurrentThread);
                await Task.Yield();
                Assert.Same(this.originalThread, Thread.CurrentThread);

                await this.TestReentrancyOfUnrelatedDependentWork();
                Assert.Same(this.originalThread, Thread.CurrentThread);

                await Task.Run(async delegate
                {
                    await this.asyncPump.SwitchToMainThreadAsync();
                    Assert.Same(this.originalThread, Thread.CurrentThread);
                });

                await this.TestReentrancyOfUnrelatedDependentWork();
                Assert.Same(this.originalThread, Thread.CurrentThread);

                this.asyncPump.Run(async delegate
                {
                    Assert.Same(this.originalThread, Thread.CurrentThread);
                    await Task.Yield();
                    Assert.Same(this.originalThread, Thread.CurrentThread);

                    await this.TestReentrancyOfUnrelatedDependentWork();
                    Assert.Same(this.originalThread, Thread.CurrentThread);

                    await Task.Run(async delegate
                    {
                        await this.asyncPump.SwitchToMainThreadAsync();
                        Assert.Same(this.originalThread, Thread.CurrentThread);
                    });

                    await this.TestReentrancyOfUnrelatedDependentWork();
                    Assert.Same(this.originalThread, Thread.CurrentThread);

                    Assert.Same(this.originalThread, Thread.CurrentThread);
                    innerCompleted = true;
                });

                await Task.Yield();
                Assert.Same(this.originalThread, Thread.CurrentThread);
                outerCompleted = true;
            });

            Assert.True(innerCompleted, "Nested Run did not complete.");
            Assert.True(outerCompleted, "Outer Run did not complete.");
        }

        [StaFact]
        public void RunSynchronouslyOffMainThreadRequiresJoinToReenterMainThreadForSameAsyncPumpInstance()
        {
            var task = Task.Run(delegate
            {
                this.asyncPump.Run(async delegate
                {
                    await this.asyncPump.SwitchToMainThreadAsync();
                    Assert.Same(this.originalThread, Thread.CurrentThread); //, "We're not on the Main thread!");
                });
            });

            this.asyncPump.Run(async delegate
            {
                // Even though it's all the same instance of AsyncPump,
                // unrelated work (work not spun off from this block) must still be
                // Joined in order to execute here.
                Assert.NotSame(task, await Task.WhenAny(task, Task.Delay(AsyncDelay / 2))); //, "The unrelated main thread work completed before the Main thread was joined.");
                using (this.joinableCollection.Join())
                {
                    this.PrintActiveTasksReport();
                    await task;
                }
            });
        }

        [StaFact]
        public void RunSynchronouslyOffMainThreadRequiresJoinToReenterMainThreadForDifferentAsyncPumpInstance()
        {
            var otherCollection = this.context.CreateCollection();
            var otherAsyncPump = this.context.CreateFactory(otherCollection);
            var task = Task.Run(delegate
            {
                otherAsyncPump.Run(async delegate
                {
                    await otherAsyncPump.SwitchToMainThreadAsync();
                    Assert.Same(this.originalThread, Thread.CurrentThread);
                });
            });

            this.asyncPump.Run(async delegate
            {
                Assert.NotSame(task, await Task.WhenAny(task, Task.Delay(AsyncDelay / 2))); //, "The unrelated main thread work completed before the Main thread was joined.");
                using (otherCollection.Join())
                {
                    await task;
                }
            });
        }

        /// <summary>
        /// Checks that posting to the SynchronizationContext.Current doesn't cause a hang.
        /// </summary>
        /// <remarks>
        /// DevDiv bug 874540 represents a hang that this test repros.
        /// </remarks>
        [StaFact]
        public void RunSwitchesToMainThreadAndPosts()
        {
            var frame = new DispatcherFrame();
            var task = Task.Run(delegate
            {
                try
                {
                    this.asyncPump.Run(async delegate
                    {
                        await this.asyncPump.SwitchToMainThreadAsync();
                        SynchronizationContext.Current.Post(s => { }, null);
                    });
                }
                finally
                {
                    frame.Continue = false;
                }
            });

            // Now let the request proceed through.
            Dispatcher.PushFrame(frame);
            task.Wait(); // rethrow exceptions.
        }

        /// <summary>
        /// Checks that posting to the SynchronizationContext.Current doesn't cause a hang.
        /// </summary>
        [StaFact]
        public void RunSwitchesToMainThreadAndPostsTwice()
        {
            ((DerivedJoinableTaskFactory)this.asyncPump).AssumeConcurrentUse = true;
            var frame = new DispatcherFrame();
            var task = Task.Run(delegate
            {
                try
                {
                    this.asyncPump.Run(async delegate
                    {
                        await this.asyncPump.SwitchToMainThreadAsync();
                        SynchronizationContext.Current.Post(s => { }, null);
                        SynchronizationContext.Current.Post(s => { }, null);
                    });
                }
                finally
                {
                    frame.Continue = false;
                }
            });

            // Now let the request proceed through.
            Dispatcher.PushFrame(frame);
            task.Wait(); // rethrow exceptions.
        }

        /// <summary>
        /// Checks that posting to the SynchronizationContext.Current doesn't cause a hang.
        /// </summary>
        [StaFact]
        public void RunSwitchesToMainThreadAndPostsTwiceDoesNotImpactJoinableTaskCompletion()
        {
            ((DerivedJoinableTaskFactory)this.asyncPump).AssumeConcurrentUse = true;
            var frame = new DispatcherFrame();
            Task task = null;
            task = Task.Run(delegate
            {
                try
                {
                    this.asyncPump.Run(async delegate
                    {
                        await this.asyncPump.SwitchToMainThreadAsync();

                        // Kick off work that should *not* impact the completion of
                        // the JoinableTask that lives within this Run delegate.
                        // And enforce the assertion by blocking the main thread until
                        // the JoinableTask is done, which would deadlock if the
                        // JoinableTask were inappropriately blocking on the completion
                        // of the posted message.
                        SynchronizationContext.Current.Post(s => { task.Wait(); }, null);

                        // Post one more time, since an implementation detail may unblock
                        // the JoinableTask for the very last posted message for reasons that
                        // don't apply for other messages.
                        SynchronizationContext.Current.Post(s => { }, null);
                    });
                }
                finally
                {
                    frame.Continue = false;
                }
            });

            // Now let the request proceed through.
            Dispatcher.PushFrame(frame);
            task.Wait(); // rethrow exceptions.
        }

        [StaFact]
        public void SwitchToMainThreadImmediatelyShouldNotHang()
        {
            ((DerivedJoinableTaskFactory)this.asyncPump).AssumeConcurrentUse = true;

            var task1Started = new AsyncManualResetEvent(false);
            var task2Started = new AsyncManualResetEvent(false);

            this.asyncPump.Run(async delegate
            {
                var child1Task = Task.Run(async delegate
                {
                    await this.asyncPump.SwitchToMainThreadAsync().GetAwaiter().YieldAndNotify(task1Started);
                });

                var child2Task = Task.Run(async delegate
                {
                    await this.asyncPump.SwitchToMainThreadAsync().GetAwaiter().YieldAndNotify(task2Started);
                });

                task1Started.WaitAsync().Wait();
                task2Started.WaitAsync().Wait();

                await child1Task;
                await child2Task;
            });
        }

        [StaFact]
        public void MultipleSwitchToMainThreadShouldNotHang()
        {
            ((DerivedJoinableTaskFactory)this.asyncPump).AssumeConcurrentUse = true;

            JoinableTask task1 = null, task2 = null;
            var taskStarted = new AsyncManualResetEvent();
            var testEnded = new AsyncManualResetEvent();
            var dependentWork1Queued = new AsyncManualResetEvent();
            var dependentWork2Queued = new AsyncManualResetEvent();
            var dependentWork1Finished = new AsyncManualResetEvent();
            var dependentWork2Finished = new AsyncManualResetEvent();

            var separatedTask = Task.Run(async delegate
            {
                task1 = this.asyncPump.RunAsync(async delegate
                {
                    await this.asyncPump.SwitchToMainThreadAsync()
                        .GetAwaiter().YieldAndNotify(dependentWork1Queued);

                    dependentWork1Finished.Set();
                });

                task2 = this.asyncPump.RunAsync(async delegate
                {
                    var collection = new JoinableTaskCollection(this.joinableCollection.Context);
                    collection.Add(task1);
                    collection.Join();

                    await this.asyncPump.SwitchToMainThreadAsync()
                        .GetAwaiter().YieldAndNotify(dependentWork2Queued);

                    dependentWork2Finished.Set();
                    await testEnded;
                });

                taskStarted.Set();
                await testEnded;
            });

            this.asyncPump.Run(async delegate
            {
                await taskStarted;
                await dependentWork1Queued;
                await dependentWork2Queued;

                var collection = new JoinableTaskCollection(this.joinableCollection.Context);
                collection.Add(task2);
                collection.Join();

                await dependentWork1Finished;
                await dependentWork2Finished;

                testEnded.Set();
            });

            this.asyncPump.Run(async delegate
            {
                using (this.joinableCollection.Join())
                {
                    await task1;
                    await task2;
                    await separatedTask;
                }
            });
        }

        [StaFact]
        public void SwitchToMainThreadWithDelayedDependencyShouldNotHang()
        {
            JoinableTask task1 = null, task2 = null;
            var taskStarted = new AsyncManualResetEvent();
            var testEnded = new AsyncManualResetEvent();
            var dependentWorkAllowed = new AsyncManualResetEvent();
            var indirectDependencyAllowed = new AsyncManualResetEvent();
            var dependentWorkQueued = new AsyncManualResetEvent();
            var dependentWorkFinished = new AsyncManualResetEvent();

            var separatedTask = Task.Run(async delegate
            {
                var taskCollection = new JoinableTaskCollection(this.context);
                var factory = new JoinableTaskFactory(taskCollection);
                task1 = this.asyncPump.RunAsync(async delegate
                {
                    await dependentWorkAllowed;
                    await factory.SwitchToMainThreadAsync()
                        .GetAwaiter().YieldAndNotify(dependentWorkQueued);

                    dependentWorkFinished.Set();
                });

                task2 = this.asyncPump.RunAsync(async delegate
                {
                    await indirectDependencyAllowed;

                    var collection = new JoinableTaskCollection(this.joinableCollection.Context);
                    collection.Add(task1);

                    await Task.Delay(AsyncDelay);
                    collection.Join();

                    await testEnded;
                });

                taskStarted.Set();
                await testEnded;
            });

            this.asyncPump.Run(async delegate
            {
                await taskStarted;
                dependentWorkAllowed.Set();
                await dependentWorkQueued;

                var collection = new JoinableTaskCollection(this.joinableCollection.Context);
                collection.Add(task2);

                collection.Join();
                indirectDependencyAllowed.Set();

                await dependentWorkFinished;

                testEnded.Set();
            });

            this.asyncPump.Run(async delegate
            {
                using (this.joinableCollection.Join())
                {
                    await task1;
                    await task2;
                    await separatedTask;
                }
            });
        }

        [StaFact]
        public void DoubleJoinedTaskDisjoinCorrectly()
        {
            JoinableTask task1 = null;
            var taskStarted = new AsyncManualResetEvent();
            var dependentFirstWorkCompleted = new AsyncManualResetEvent();
            var dependentSecondWorkAllowed = new AsyncManualResetEvent();
            var mainThreadDependentSecondWorkQueued = new AsyncManualResetEvent();
            var testEnded = new AsyncManualResetEvent();

            var separatedTask = Task.Run(async delegate
            {
                task1 = this.asyncPump.RunAsync(async delegate
                {
                    await this.asyncPump.SwitchToMainThreadAsync();
                    await TaskScheduler.Default;

                    dependentFirstWorkCompleted.Set();
                    await dependentSecondWorkAllowed;

                    await this.asyncPump.SwitchToMainThreadAsync()
                        .GetAwaiter().YieldAndNotify(mainThreadDependentSecondWorkQueued);
                });

                taskStarted.Set();
                await testEnded;
            });

            this.asyncPump.Run(async delegate
            {
                await taskStarted;

                var collection1 = new JoinableTaskCollection(this.joinableCollection.Context);
                collection1.Add(task1);
                var collection2 = new JoinableTaskCollection(this.joinableCollection.Context);
                collection2.Add(task1);

                using (collection1.Join())
                {
                    using (collection2.Join())
                    {
                    }

                    await dependentFirstWorkCompleted;
                }

                dependentSecondWorkAllowed.Set();
                await mainThreadDependentSecondWorkQueued;

                await Task.Delay(AsyncDelay);
                await Task.Yield();

                Assert.False(task1.IsCompleted);

                testEnded.Set();
            });

            this.asyncPump.Run(async delegate
            {
                using (this.joinableCollection.Join())
                {
                    await task1;
                    await separatedTask;
                }
            });
        }

        [StaFact]
        public void DoubleIndirectJoinedTaskDisjoinCorrectly()
        {
            JoinableTask task1 = null, task2 = null, task3 = null;
            var taskStarted = new AsyncManualResetEvent();
            var dependentFirstWorkCompleted = new AsyncManualResetEvent();
            var dependentSecondWorkAllowed = new AsyncManualResetEvent();
            var mainThreadDependentSecondWorkQueued = new AsyncManualResetEvent();
            var testEnded = new AsyncManualResetEvent();

            var separatedTask = Task.Run(async delegate
            {
                task1 = this.asyncPump.RunAsync(async delegate
                {
                    await this.asyncPump.SwitchToMainThreadAsync();
                    await TaskScheduler.Default;

                    dependentFirstWorkCompleted.Set();
                    await dependentSecondWorkAllowed;

                    await this.asyncPump.SwitchToMainThreadAsync()
                        .GetAwaiter().YieldAndNotify(mainThreadDependentSecondWorkQueued);
                });

                task2 = this.asyncPump.RunAsync(async delegate
                {
                    var collection = new JoinableTaskCollection(this.joinableCollection.Context);
                    collection.Add(task1);
                    using (collection.Join())
                    {
                        await testEnded;
                    }
                });

                task3 = this.asyncPump.RunAsync(async delegate
                {
                    var collection = new JoinableTaskCollection(this.joinableCollection.Context);
                    collection.Add(task1);
                    using (collection.Join())
                    {
                        await testEnded;
                    }
                });

                taskStarted.Set();
                await testEnded;
            });

            var waitCountingJTF = new WaitCountingJoinableTaskFactory(this.asyncPump.Context);
            waitCountingJTF.Run(async delegate
            {
                await taskStarted;

                var collection = new JoinableTaskCollection(this.joinableCollection.Context);
                collection.Add(task2);
                collection.Add(task3);

                using (collection.Join())
                {
                    await dependentFirstWorkCompleted;
                }

                int waitCountBeforeSecondWork = waitCountingJTF.WaitCount;
                dependentSecondWorkAllowed.Set();
                await Task.Delay(AsyncDelay / 2);
                await mainThreadDependentSecondWorkQueued;

                await Task.Delay(AsyncDelay / 2);
                await Task.Yield();

                // we expect 3 switching from two delay one yield call.  We don't want one triggered by Task1.
                Assert.True(waitCountingJTF.WaitCount - waitCountBeforeSecondWork <= 3);
                Assert.False(task1.IsCompleted);

                testEnded.Set();
            });

            this.asyncPump.Run(async delegate
            {
                using (this.joinableCollection.Join())
                {
                    await task1;
                    await task2;
                    await task3;
                    await separatedTask;
                }
            });
        }

        /// <summary>
        /// Main -> Task1, Main -> Task2, Task1 &lt;-&gt; Task2 (loop dependency between Task1 and Task2.
        /// </summary>
        [StaFact]
        public void JoinWithLoopDependentTasks()
        {
            JoinableTask task1 = null, task2 = null;
            var taskStarted = new AsyncManualResetEvent();
            var testStarted = new AsyncManualResetEvent();
            var task1Prepared = new AsyncManualResetEvent();
            var task2Prepared = new AsyncManualResetEvent();
            var mainThreadDependentFirstWorkQueued = new AsyncManualResetEvent();
            var dependentFirstWorkCompleted = new AsyncManualResetEvent();
            var dependentSecondWorkAllowed = new AsyncManualResetEvent();
            var dependentSecondWorkCompleted = new AsyncManualResetEvent();
            var dependentThirdWorkAllowed = new AsyncManualResetEvent();
            var mainThreadDependentThirdWorkQueued = new AsyncManualResetEvent();
            var testEnded = new AsyncManualResetEvent();

            var separatedTask = Task.Run(async delegate
            {
                task1 = this.asyncPump.RunAsync(async delegate
                {
                    await taskStarted;
                    await testStarted;
                    var collection = new JoinableTaskCollection(this.joinableCollection.Context);
                    collection.Add(task2);
                    using (collection.Join())
                    {
                        task1Prepared.Set();

                        await this.asyncPump.SwitchToMainThreadAsync()
                            .GetAwaiter().YieldAndNotify(mainThreadDependentFirstWorkQueued);
                        await TaskScheduler.Default;

                        dependentFirstWorkCompleted.Set();

                        await dependentSecondWorkAllowed;
                        await this.asyncPump.SwitchToMainThreadAsync();
                        await TaskScheduler.Default;

                        dependentSecondWorkCompleted.Set();

                        await dependentThirdWorkAllowed;
                        await this.asyncPump.SwitchToMainThreadAsync()
                            .GetAwaiter().YieldAndNotify(mainThreadDependentThirdWorkQueued);
                    }
                });

                task2 = this.asyncPump.RunAsync(async delegate
                {
                    await taskStarted;
                    await testStarted;
                    var collection = new JoinableTaskCollection(this.joinableCollection.Context);
                    collection.Add(task1);
                    using (collection.Join())
                    {
                        task2Prepared.Set();
                        await testEnded;
                    }
                });

                taskStarted.Set();
                await testEnded;
            });

            var waitCountingJTF = new WaitCountingJoinableTaskFactory(this.asyncPump.Context);
            waitCountingJTF.Run(async delegate
            {
                await taskStarted;
                testStarted.Set();
                await task1Prepared;
                await task2Prepared;

                var collection1 = new JoinableTaskCollection(this.joinableCollection.Context);
                collection1.Add(task1);
                var collection2 = new JoinableTaskCollection(this.joinableCollection.Context);
                collection2.Add(task2);
                await mainThreadDependentFirstWorkQueued;

                using (collection2.Join())
                {
                    using (collection1.Join())
                    {
                        await dependentFirstWorkCompleted;
                    }

                    dependentSecondWorkAllowed.Set();
                    await dependentSecondWorkCompleted;
                }

                int waitCountBeforeSecondWork = waitCountingJTF.WaitCount;
                dependentThirdWorkAllowed.Set();

                await Task.Delay(AsyncDelay / 2);
                await mainThreadDependentThirdWorkQueued;

                await Task.Delay(AsyncDelay / 2);
                await Task.Yield();

                // we expect 3 switching from two delay one yield call.  We don't want one triggered by Task1.
                Assert.True(waitCountingJTF.WaitCount - waitCountBeforeSecondWork <= 3);
                Assert.False(task1.IsCompleted);

                testEnded.Set();
            });

            this.asyncPump.Run(async delegate
            {
                using (this.joinableCollection.Join())
                {
                    await task1;
                    await task2;
                    await separatedTask;
                }
            });
        }

        [StaFact]
        public void DeepLoopedJoinedTaskDisjoinCorrectly()
        {
            JoinableTask task1 = null, task2 = null, task3 = null, task4 = null, task5 = null;
            var taskStarted = new AsyncManualResetEvent();
            var task2Prepared = new AsyncManualResetEvent();
            var task3Prepared = new AsyncManualResetEvent();
            var task4Prepared = new AsyncManualResetEvent();
            var dependentFirstWorkCompleted = new AsyncManualResetEvent();
            var dependentSecondWorkAllowed = new AsyncManualResetEvent();
            var mainThreadDependentSecondWorkQueued = new AsyncManualResetEvent();
            var testEnded = new AsyncManualResetEvent();

            var separatedTask = Task.Run(async delegate
            {
                task1 = this.asyncPump.RunAsync(async delegate
                {
                    await taskStarted;

                    var collection = new JoinableTaskCollection(this.joinableCollection.Context);
                    collection.Add(task1);
                    using (collection.Join())
                    {
                        await this.asyncPump.SwitchToMainThreadAsync();
                        await TaskScheduler.Default;

                        dependentFirstWorkCompleted.Set();
                        await dependentSecondWorkAllowed;

                        await this.asyncPump.SwitchToMainThreadAsync()
                            .GetAwaiter().YieldAndNotify(mainThreadDependentSecondWorkQueued);
                    }
                });

                task2 = this.asyncPump.RunAsync(async delegate
                {
                    var collection = new JoinableTaskCollection(this.joinableCollection.Context);
                    collection.Add(task1);
                    using (collection.Join())
                    {
                        task2Prepared.Set();
                        await testEnded;
                    }
                });

                task3 = this.asyncPump.RunAsync(async delegate
                {
                    await taskStarted;

                    var collection = new JoinableTaskCollection(this.joinableCollection.Context);
                    collection.Add(task2);
                    collection.Add(task4);
                    using (collection.Join())
                    {
                        task3Prepared.Set();
                        await testEnded;
                    }
                });

                task4 = this.asyncPump.RunAsync(async delegate
                {
                    var collection = new JoinableTaskCollection(this.joinableCollection.Context);
                    collection.Add(task2);
                    collection.Add(task3);
                    using (collection.Join())
                    {
                        task4Prepared.Set();
                        await testEnded;
                    }
                });

                task5 = this.asyncPump.RunAsync(async delegate
                {
                    var collection = new JoinableTaskCollection(this.joinableCollection.Context);
                    collection.Add(task3);
                    using (collection.Join())
                    {
                        await testEnded;
                    }
                });

                taskStarted.Set();
                await testEnded;
            });

            var waitCountingJTF = new WaitCountingJoinableTaskFactory(this.asyncPump.Context);
            waitCountingJTF.Run(async delegate
            {
                await taskStarted;
                await task2Prepared;
                await task3Prepared;
                await task4Prepared;

                var collection = new JoinableTaskCollection(this.joinableCollection.Context);
                collection.Add(task5);

                using (collection.Join())
                {
                    await dependentFirstWorkCompleted;
                }

                int waitCountBeforeSecondWork = waitCountingJTF.WaitCount;
                dependentSecondWorkAllowed.Set();

                await Task.Delay(AsyncDelay / 2);
                await mainThreadDependentSecondWorkQueued;

                await Task.Delay(AsyncDelay / 2);
                await Task.Yield();

                // we expect 3 switching from two delay one yield call.  We don't want one triggered by Task1.
                Assert.True(waitCountingJTF.WaitCount - waitCountBeforeSecondWork <= 3);
                Assert.False(task1.IsCompleted);

                testEnded.Set();
            });

            this.asyncPump.Run(async delegate
            {
                using (this.joinableCollection.Join())
                {
                    await task1;
                    await task2;
                    await task3;
                    await task4;
                    await task5;
                    await separatedTask;
                }
            });
        }

        [StaFact]
        public void JoinRejectsSubsequentWork()
        {
            bool outerCompleted = false;

            var mainThreadDependentWorkQueued = new AsyncManualResetEvent();
            var dependentWorkCompleted = new AsyncManualResetEvent();
            var joinReverted = new AsyncManualResetEvent();
            var postJoinRevertedWorkQueued = new AsyncManualResetEvent();
            var postJoinRevertedWorkExecuting = new AsyncManualResetEvent();
            var unrelatedTask = Task.Run(async delegate
            {
                // STEP 2
                await this.asyncPump.SwitchToMainThreadAsync()
                .GetAwaiter().YieldAndNotify(mainThreadDependentWorkQueued);

                // STEP 4
                Assert.Same(this.originalThread, Thread.CurrentThread);
                dependentWorkCompleted.Set();
                await joinReverted.WaitAsync().ConfigureAwait(false);

                // STEP 6
                Assert.NotSame(this.originalThread, Thread.CurrentThread);
                await this.asyncPump.SwitchToMainThreadAsync().GetAwaiter().YieldAndNotify(postJoinRevertedWorkQueued, postJoinRevertedWorkExecuting);

                // STEP 8
                Assert.Same(this.originalThread, Thread.CurrentThread);
            });

            this.asyncPump.Run(async delegate
            {
                // STEP 1
                Assert.Same(this.originalThread, Thread.CurrentThread);
                await Task.Yield();
                Assert.Same(this.originalThread, Thread.CurrentThread);
                await mainThreadDependentWorkQueued.WaitAsync();

                // STEP 3
                using (this.joinableCollection.Join())
                {
                    await dependentWorkCompleted.WaitAsync();
                }

                // STEP 5
                joinReverted.Set();
                var releasingTask = await Task.WhenAny(unrelatedTask, postJoinRevertedWorkQueued.WaitAsync());
                if (releasingTask == unrelatedTask & unrelatedTask.IsFaulted)
                {
                    unrelatedTask.GetAwaiter().GetResult(); // rethrow an error that has already occurred.
                }

                // STEP 7
                var executingWaitTask = postJoinRevertedWorkExecuting.WaitAsync();
                Assert.NotSame(executingWaitTask, await Task.WhenAny(executingWaitTask, Task.Delay(AsyncDelay))); //, "Main thread work from unrelated task should not have executed.");

                await Task.Yield();
                Assert.Same(this.originalThread, Thread.CurrentThread);
                outerCompleted = true;
            });

            Assert.True(outerCompleted, "Outer Run did not complete.");

            // Allow background task's last Main thread work to finish.
            Assert.False(unrelatedTask.IsCompleted);
            this.asyncPump.Run(async delegate
            {
                using (this.joinableCollection.Join())
                {
                    await unrelatedTask;
                }
            });
        }

        [StaFact]
        public void SyncContextRestoredAfterRun()
        {
            var syncContext = SynchronizationContext.Current;
            Assert.NotNull(syncContext); // We need a non-null sync context for this test to be useful.

            this.asyncPump.Run(async delegate
            {
                await Task.Yield();
            });

            Assert.Same(syncContext, SynchronizationContext.Current);
        }

        [StaFact]
        public void BackgroundSynchronousTransitionsToUIThreadSynchronous()
        {
            var task = Task.Run(delegate
            {
                this.asyncPump.Run(async delegate
                {
                    Assert.NotSame(this.originalThread, Thread.CurrentThread);
                    await this.asyncPump.SwitchToMainThreadAsync();

                    // The scenario here is that some code calls out, then back in, via a synchronous interface
                    this.asyncPump.Run(async delegate
                {
                    await Task.Yield();
                    await this.TestReentrancyOfUnrelatedDependentWork();
                });
                });
            });

            // Avoid a deadlock while waiting for test to complete.
            this.asyncPump.Run(async delegate
            {
                using (this.joinableCollection.Join())
                {
                    await task;
                }
            });
        }

        [StaFact]
        public void SwitchToMainThreadAwaiterReappliesAsyncLocalSyncContextOnContinuation()
        {
            var task = Task.Run(delegate
            {
                this.asyncPump.Run(async delegate
                {
                    Assert.NotSame(this.originalThread, Thread.CurrentThread);

                    // Switching to the main thread here will get us the SynchronizationContext we need,
                    // and the awaiter's GetResult() should apply the AsyncLocal sync context as well
                    // to avoid deadlocks later.
                    await this.asyncPump.SwitchToMainThreadAsync();

                    await this.TestReentrancyOfUnrelatedDependentWork();

                    // The scenario here is that some code calls out, then back in, via a synchronous interface
                    this.asyncPump.Run(async delegate
                {
                    await Task.Yield();
                    await this.TestReentrancyOfUnrelatedDependentWork();
                });
                });
            });

            // Avoid a deadlock while waiting for test to complete.
            this.asyncPump.Run(async delegate
            {
                using (this.joinableCollection.Join())
                {
                    await task;
                }
            });
        }

        [StaFact]
        public void NestedJoinsDistinctAsyncPumps()
        {
            const int nestLevels = 3;
            MockAsyncService outerService = null;
            for (int level = 0; level < nestLevels; level++)
            {
                outerService = new MockAsyncService(this.asyncPump.Context, outerService);
            }

            var operationTask = outerService.OperationAsync();

            this.asyncPump.Run(async delegate
            {
                await outerService.StopAsync(operationTask);
            });

            Assert.True(operationTask.IsCompleted);
        }

        [StaFact]
        public void SynchronousTaskStackMaintainedCorrectly()
        {
            this.asyncPump.Run(async delegate
            {
                this.asyncPump.Run(() => Task.FromResult<bool>(true));
                await Task.Yield();
            });
        }

        [StaFact]
        public void SynchronousTaskStackMaintainedCorrectlyWithForkedTask()
        {
            var innerTaskWaitingSwitching = new AsyncManualResetEvent();

            this.asyncPump.Run(async delegate
            {
                Task innerTask = null;
                this.asyncPump.Run(delegate
                {
                    // We need simulate a scenario that the task is completed without any yielding,
                    // but the queue of the Joinable task is not empty at that point,
                    // so the synchronous JoinableTask doesn't need any blocking time, but it is completed later.
                    innerTask = Task.Run(async delegate
                {
                    await this.asyncPump.SwitchToMainThreadAsync().GetAwaiter().YieldAndNotify(innerTaskWaitingSwitching);
                });

                    innerTaskWaitingSwitching.WaitAsync().Wait();
                    return Task.FromResult(true);
                });

                await Task.Yield();

                // Now, get rid of the innerTask
                await innerTask;
            });
        }

        [StaFact(Skip = "ignored")]
        public void SynchronousTaskStackMaintainedCorrectlyWithForkedTask2()
        {
            var innerTaskWaiting = new AsyncManualResetEvent();

            // This test simulates that we have an inner task starts to switch to main thread after the joinable task is compeleted.
            // Because completed task won't be tracked in the dependent chain, waiting it causes a deadlock.  This could be a potential problem.
            this.asyncPump.Run(async delegate
            {
                Task innerTask = null;
                this.asyncPump.Run(delegate
                {
                    innerTask = Task.Run(async delegate
                    {
                        await innerTaskWaiting.WaitAsync();
                        await this.asyncPump.SwitchToMainThreadAsync();
                    });

                    return Task.FromResult(true);
                });

                innerTaskWaiting.Set();
                await Task.Yield();

                // Now, get rid of the innerTask
                await innerTask;
            });
        }

        [StaFact]
        public void RunSynchronouslyKicksOffReturnsThenSyncBlocksStillRequiresJoin()
        {
            var mainThreadNowBlocking = new AsyncManualResetEvent();
            Task task = null;
            this.asyncPump.Run(delegate
            {
                task = Task.Run(async delegate
                {
                    await mainThreadNowBlocking.WaitAsync();
                    await this.asyncPump.SwitchToMainThreadAsync();
                });

                return TplExtensions.CompletedTask;
            });

            this.asyncPump.Run(async delegate
            {
                mainThreadNowBlocking.Set();
                Assert.NotSame(task, await Task.WhenAny(task, Task.Delay(AsyncDelay / 2)));
                using (this.joinableCollection.Join())
                {
                    await task;
                }
            });
        }

        [StaFact]
        public void KickOffAsyncWorkFromMainThreadThenBlockOnIt()
        {
            var joinable = this.asyncPump.RunAsync(async delegate
            {
                await this.SomeOperationThatMayBeOnMainThreadAsync();
            });

            this.asyncPump.Run(async delegate
            {
                using (this.joinableCollection.Join())
                {
                    await joinable.Task;
                }
            });
        }

        [StaFact]
        public void KickOffDeepAsyncWorkFromMainThreadThenBlockOnIt()
        {
            var joinable = this.asyncPump.RunAsync(async delegate
            {
                await this.SomeOperationThatUsesMainThreadViaItsOwnAsyncPumpAsync();
            });

            this.asyncPump.Run(async delegate
            {
                using (this.joinableCollection.Join())
                {
                    await joinable.Task;
                }
            });
        }

        [StaFact]
        public void BeginAsyncCompleteSync()
        {
            Task task = this.asyncPump.RunAsync(
                () => this.SomeOperationThatUsesMainThreadViaItsOwnAsyncPumpAsync()).Task;
            Assert.False(task.IsCompleted);
            this.asyncPump.CompleteSynchronously(this.joinableCollection, task);
        }

        [StaFact]
        public void BeginAsyncYieldsWhenDelegateYieldsOnUIThread()
        {
            bool afterYieldReached = false;
            Task task = this.asyncPump.RunAsync(async delegate
            {
                await Task.Yield();
                afterYieldReached = true;
            }).Task;

            Assert.False(afterYieldReached);
            this.asyncPump.CompleteSynchronously(this.joinableCollection, task);
            Assert.True(afterYieldReached);
        }

        [StaFact]
        public void BeginAsyncYieldsWhenDelegateYieldsOffUIThread()
        {
            bool afterYieldReached = false;
            var backgroundThreadWorkDoneEvent = new AsyncManualResetEvent();
            Task task = this.asyncPump.RunAsync(async delegate
            {
                await backgroundThreadWorkDoneEvent;
                afterYieldReached = true;
            }).Task;

            Assert.False(afterYieldReached);
            backgroundThreadWorkDoneEvent.Set();
            this.asyncPump.CompleteSynchronously(this.joinableCollection, task);
            Assert.True(afterYieldReached);
        }

        [StaFact]
        public void BeginAsyncYieldsToAppropriateContext()
        {
            var backgroundWork = Task.Run<Task>(delegate
            {
                return this.asyncPump.RunAsync(async delegate
                {
                    // Verify that we're on a background thread and stay there.
                    Assert.NotSame(this.originalThread, Thread.CurrentThread);
                    await Task.Yield();
                    Assert.NotSame(this.originalThread, Thread.CurrentThread);

                    // Now explicitly get on the Main thread, and verify that we stay there.
                    await this.asyncPump.SwitchToMainThreadAsync();
                    Assert.Same(this.originalThread, Thread.CurrentThread);
                    await Task.Yield();
                    Assert.Same(this.originalThread, Thread.CurrentThread);
                }).Task;
            }).Result;

            this.asyncPump.CompleteSynchronously(this.joinableCollection, backgroundWork);
        }

        [StaFact]
        public void RunSynchronouslyYieldsToAppropriateContext()
        {
            for (int i = 0; i < 100; i++)
            {
                var backgroundWork = Task.Run(delegate
                {
                    this.asyncPump.Run(async delegate
                    {
                        // Verify that we're on a background thread and stay there.
                        Assert.NotSame(this.originalThread, Thread.CurrentThread);
                        await Task.Yield();
                        Assert.NotSame(this.originalThread, Thread.CurrentThread);

                        // Now explicitly get on the Main thread, and verify that we stay there.
                        await this.asyncPump.SwitchToMainThreadAsync();
                        Assert.Same(this.originalThread, Thread.CurrentThread);
                        await Task.Yield();
                        Assert.Same(this.originalThread, Thread.CurrentThread);
                    });
                });

                this.asyncPump.CompleteSynchronously(this.joinableCollection, backgroundWork);
            }
        }

        [StaFact]
        public void BeginAsyncOnMTAKicksOffOtherAsyncPumpWorkCanCompleteSynchronouslySwitchFirst()
        {
            var otherCollection = this.asyncPump.Context.CreateCollection();
            var otherPump = this.asyncPump.Context.CreateFactory(otherCollection);
            bool taskFinished = false;
            var switchPended = new ManualResetEventSlim();

            // Kick off the BeginAsync work from a background thread that has no special
            // affinity to the main thread.
            var joinable = Task.Run(delegate
            {
                return this.asyncPump.RunAsync(async delegate
                {
                    await Task.Yield();
                    var awaiter = otherPump.SwitchToMainThreadAsync().GetAwaiter();
                    Assert.False(awaiter.IsCompleted);
                    var continuationFinished = new AsyncManualResetEvent();
                    awaiter.OnCompleted(delegate
                    {
                        taskFinished = true;
                        continuationFinished.Set();
                    });
                    switchPended.Set();
                    await continuationFinished;
                });
            }).Result;

            Assert.False(joinable.Task.IsCompleted);
            switchPended.Wait();
            joinable.Join();
            Assert.True(taskFinished);
            Assert.True(joinable.Task.IsCompleted);
        }

        [StaFact]
        public void BeginAsyncOnMTAKicksOffOtherAsyncPumpWorkCanCompleteSynchronouslyJoinFirst()
        {
            var otherCollection = this.asyncPump.Context.CreateCollection();
            var otherPump = this.asyncPump.Context.CreateFactory(otherCollection);
            bool taskFinished = false;
            var joinedEvent = new AsyncManualResetEvent();

            // Kick off the BeginAsync work from a background thread that has no special
            // affinity to the main thread.
            var joinable = Task.Run(delegate
            {
                return this.asyncPump.RunAsync(async delegate
                {
                    await joinedEvent;
                    await otherPump.SwitchToMainThreadAsync();
                    taskFinished = true;
                });
            }).Result;

            Assert.False(joinable.Task.IsCompleted);
            this.asyncPump.Run(async delegate
            {
                var awaitable = joinable.JoinAsync();
                joinedEvent.Set();
                await awaitable;
            });
            Assert.True(taskFinished);
            Assert.True(joinable.Task.IsCompleted);
        }

        [StaFact]
        public void BeginAsyncWithResultOnMTAKicksOffOtherAsyncPumpWorkCanCompleteSynchronously()
        {
            var otherCollection = this.asyncPump.Context.CreateCollection();
            var otherPump = this.asyncPump.Context.CreateFactory(otherCollection);
            bool taskFinished = false;

            // Kick off the BeginAsync work from a background thread that has no special
            // affinity to the main thread.
            var joinable = Task.Run(delegate
            {
                return this.asyncPump.RunAsync(async delegate
                {
                    await Task.Yield();
                    await otherPump.SwitchToMainThreadAsync();
                    taskFinished = true;
                    return 5;
                });
            }).Result;

            Assert.False(joinable.Task.IsCompleted);
            var result = joinable.Join();
            Assert.Equal<int>(5, result);
            Assert.True(taskFinished);
            Assert.True(joinable.Task.IsCompleted);
        }

        [StaFact]
        public void JoinCancellation()
        {
            // Kick off the BeginAsync work from a background thread that has no special
            // affinity to the main thread.
            var joinable = this.asyncPump.RunAsync(async delegate
            {
                await Task.Yield();
                await this.asyncPump.SwitchToMainThreadAsync();
                await Task.Delay(AsyncDelay);
            });

            Assert.False(joinable.Task.IsCompleted);
            var cts = new CancellationTokenSource(AsyncDelay / 4);
            Assert.Throws<OperationCanceledException>(() => joinable.Join(cts.Token));
        }

        [StaFact]
        public void RunSynchronouslyTaskOfTWithFireAndForgetMethod()
        {
            this.asyncPump.Run(async delegate
            {
                await Task.Yield();
                SomeFireAndForgetMethod();
                await Task.Yield();
                await Task.Delay(AsyncDelay);
            });
        }

        [StaFact]
        public void SendToSyncContextCapturedFromWithinRunSynchronously()
        {
            var countdownEvent = new AsyncCountdownEvent(2);
            var state = new GenericParameterHelper(3);
            SynchronizationContext syncContext = null;
            Task sendFromWithinRunSync = null;
            this.asyncPump.Run(delegate
            {
                syncContext = SynchronizationContext.Current;

                bool executed1 = false;
                syncContext.Send(s => { Assert.Same(this.originalThread, Thread.CurrentThread); Assert.Same(state, s); executed1 = true; }, state);
                Assert.True(executed1);

                // And from another thread.  But the Main thread is "busy" in a synchronous block,
                // so the Send isn't expected to get in right away.  So spin off a task to keep the Send
                // in a wait state until it's finally able to get through.
                // This tests that Send can work even if not immediately.
                sendFromWithinRunSync = Task.Run(delegate
            {
                bool executed2 = false;
                syncContext.Send(s =>
            {
                try
                {
                    Assert.Same(this.originalThread, Thread.CurrentThread);
                    Assert.Same(state, s);
                    executed2 = true;
                }
                finally
                {
                    // Allow the message pump to exit.
                    countdownEvent.Signal();
                }
            }, state);
                Assert.True(executed2);
            });

                return TplExtensions.CompletedTask;
            });

            // From the Main thread.
            bool executed3 = false;
            syncContext.Send(s => { Assert.Same(this.originalThread, Thread.CurrentThread); Assert.Same(state, s); executed3 = true; }, state);
            Assert.True(executed3);

            // And from another thread.
            var frame = new DispatcherFrame();
            var task = Task.Run(delegate
            {
                try
                {
                    bool executed4 = false;
                    syncContext.Send(s =>
                    {
                        Assert.Same(this.originalThread, Thread.CurrentThread);
                        Assert.Same(state, s);
                        executed4 = true;
                    }, state);
                    Assert.True(executed4);
                }
                finally
                {
                    // Allow the message pump to exit.
                    countdownEvent.Signal();
                }
            });

            countdownEvent.WaitAsync().ContinueWith(_ => frame.Continue = false, TaskScheduler.Default);

            Dispatcher.PushFrame(frame);

            // throw exceptions for any failures.
            task.Wait();
            sendFromWithinRunSync.Wait();
        }

        [StaFact]
        public void SendToSyncContextCapturedAfterSwitchingToMainThread()
        {
            var frame = new DispatcherFrame();
            var state = new GenericParameterHelper(3);
            SynchronizationContext syncContext = null;
            var task = Task.Run(async delegate
            {
                try
                {
                    // starting on a worker thread, we switch to the Main thread.
                    await this.asyncPump.SwitchToMainThreadAsync();
                    syncContext = SynchronizationContext.Current;

                    bool executed1 = false;
                    syncContext.Send(s => { Assert.Same(this.originalThread, Thread.CurrentThread); Assert.Same(state, s); executed1 = true; }, state);
                    Assert.True(executed1);

                    await TaskScheduler.Default;

                    bool executed2 = false;
                    syncContext.Send(s => { Assert.Same(this.originalThread, Thread.CurrentThread); Assert.Same(state, s); executed2 = true; }, state);
                    Assert.True(executed2);
                }
                finally
                {
                    // Allow the pushed message pump frame to exit.
                    frame.Continue = false;
                }
            });

            // Open message pump so the background thread can switch to the Main thread.
            Dispatcher.PushFrame(frame);

            task.Wait(); // observe any exceptions thrown.
        }

        /// <summary>
        /// This test verifies that in the event that a Run method executes a delegate that
        /// invokes modal UI, where the WPF dispatcher would normally process Posted messages, that our
        /// applied SynchronizationContext will facilitate the same expedited message delivery.
        /// </summary>
        [StaFact]
        public void PostedMessagesAlsoSentToDispatcher()
        {
            this.asyncPump.Run(delegate
            {
                var syncContext = SynchronizationContext.Current; // simulate someone who has captured our own sync context.
                var frame = new DispatcherFrame();
                Exception ex = null;
                using (this.context.SuppressRelevance())
                { // simulate some kind of sync context hand-off that doesn't flow execution context.
                    Task.Run(delegate
            {
                // This post will only get a chance for processing
                syncContext.Post(
                state =>
                {
                    try
                    {
                        Assert.Same(this.originalThread, Thread.CurrentThread);
                    }
                    catch (Exception e)
                    {
                        ex = e;
                    }
                    finally
                    {
                        frame.Continue = false;
                    }
                },
                null);
            });
                }

                // Now simulate the display of modal UI by pushing an unfiltered message pump onto the stack.
                // This will hang unless the message gets processed.
                Dispatcher.PushFrame(frame);

                if (ex != null)
                {
                    Assert.True(false, $"Posted message threw an exception: {ex}");
                }

                return TplExtensions.CompletedTask;
            });
        }

        [StaFact]
        public void StackOverflowAvoidance()
        {
            Task backgroundTask = null;
            var mainThreadUnblocked = new AsyncManualResetEvent();
            var otherCollection = this.context.CreateCollection();
            var otherPump = this.context.CreateFactory(otherCollection);
            var frame = new DispatcherFrame();
            otherPump.Run(delegate
            {
                this.asyncPump.Run(delegate
                {
                    backgroundTask = Task.Run(async delegate
                    {
                        using (this.joinableCollection.Join())
                        {
                            await mainThreadUnblocked;
                            await this.asyncPump.SwitchToMainThreadAsync();
                            frame.Continue = false;
                        }
                    });

                    return TplExtensions.CompletedTask;
                });

                return TplExtensions.CompletedTask;
            });

            mainThreadUnblocked.Set();

            // The rest of this isn't strictly necessary for the hang, but it gets the test
            // to wait till the background task has either succeeded, or failed.
            Dispatcher.PushFrame(frame);
        }

        [StaFact]
        public void MainThreadTaskSchedulerDoesNotInlineWhileQueuingTasks()
        {
            var frame = new DispatcherFrame();
            var uiBoundWork = Task.Run(
                async delegate
                {
                    await this.asyncPump.SwitchToMainThreadAsync();
                    frame.Continue = false;
                });

            Assert.True(frame.Continue, "The UI bound work should not have executed yet.");
            Dispatcher.PushFrame(frame);
        }

        [StaFact]
        public void JoinControllingSelf()
        {
            var runSynchronouslyExited = new AsyncManualResetEvent();
            var unblockMainThread = new ManualResetEventSlim();
            Task backgroundTask = null, uiBoundWork;
            var frame = new DispatcherFrame();
            this.asyncPump.Run(delegate
            {
                backgroundTask = Task.Run(async delegate
                {
                    await runSynchronouslyExited;
                    try
                    {
                        using (this.joinableCollection.Join())
                        {
                            unblockMainThread.Set();
                        }
                    }
                    catch
                    {
                        unblockMainThread.Set();
                        throw;
                    }
                });

                return TplExtensions.CompletedTask;
            });

            uiBoundWork = Task.Run(
                async delegate
                {
                    await this.asyncPump.SwitchToMainThreadAsync();
                    frame.Continue = false;
                });

            runSynchronouslyExited.Set();
            unblockMainThread.Wait();
            Dispatcher.PushFrame(frame);
            backgroundTask.GetAwaiter().GetResult(); // rethrow any exceptions
        }

        [StaFact]
        public void JoinWorkStealingRetainsThreadAffinityUI()
        {
            bool synchronousCompletionStarting = false;
            var asyncTask = this.asyncPump.RunAsync(async delegate
            {
                int iterationsRemaining = 20;
                while (iterationsRemaining > 0)
                {
                    await Task.Yield();
                    Assert.Same(this.originalThread, Thread.CurrentThread);

                    if (synchronousCompletionStarting)
                    {
                        iterationsRemaining--;
                    }
                }
            }).Task;

            var frame = new DispatcherFrame();

            Task.Run(delegate
            {
                synchronousCompletionStarting = true;
                this.asyncPump.CompleteSynchronously(this.joinableCollection, asyncTask);
                Assert.True(asyncTask.IsCompleted);
                frame.Continue = false;
            });

            Dispatcher.PushFrame(frame);
            asyncTask.Wait(); // realize any exceptions
        }

        [StaFact]
        public void JoinWorkStealingRetainsThreadAffinityBackground()
        {
            bool synchronousCompletionStarting = false;
            var asyncTask = Task.Run(delegate
            {
                return this.asyncPump.RunAsync(async delegate
                {
                    int iterationsRemaining = 20;
                    while (iterationsRemaining > 0)
                    {
                        await Task.Yield();
                        Assert.NotSame(this.originalThread, Thread.CurrentThread);

                        if (synchronousCompletionStarting)
                        {
                            iterationsRemaining--;
                        }
                    }

                    await this.asyncPump.SwitchToMainThreadAsync();
                    for (int i = 0; i < 20; i++)
                    {
                        Assert.Same(this.originalThread, Thread.CurrentThread);
                        await Task.Yield();
                    }
                });
            });

            synchronousCompletionStarting = true;
            this.asyncPump.CompleteSynchronously(this.joinableCollection, asyncTask);
            Assert.True(asyncTask.IsCompleted);
            asyncTask.Wait(); // realize any exceptions
        }

        /// <summary>
        /// Verifies that yields in a BeginAsynchronously delegate still retain their
        /// ability to execute continuations on-demand when executed within a Join.
        /// </summary>
        [StaFact]
        public void BeginAsyncThenJoinOnMainThread()
        {
            var joinable = this.asyncPump.RunAsync(async delegate
            {
                await Task.Yield();
                await Task.Yield();
            });
            joinable.Join(); // this Join will "host" the first and second continuations.
        }

        /// <summary>
        /// Verifies that yields in a BeginAsynchronously delegate still retain their
        /// ability to execute continuations on-demand from a Join call later on
        /// the main thread.
        /// </summary>
        /// <remarks>
        /// This test allows the first continuation to naturally execute as if it were
        /// asynchronous.  Then it intercepts the main thread and Joins the original task,
        /// that has one continuation scheduled and another not yet scheduled.
        /// This test verifies that continuations retain an appropriate SynchronizationContext
        /// that will avoid deadlocks when async operations are synchronously blocked on.
        /// </remarks>
        [StaFact]
        public void BeginAsyncThenJoinOnMainThreadLater()
        {
            var frame = new DispatcherFrame();
            var firstYield = new AsyncManualResetEvent();
            var startingJoin = new AsyncManualResetEvent();
            ((DerivedJoinableTaskFactory)this.asyncPump).AssumeConcurrentUse = true;

            var joinable = this.asyncPump.RunAsync(async delegate
            {
                await Task.Yield();
                firstYield.Set();
                await startingJoin;
                frame.Continue = false;
            });

            var forcingFactor = Task.Run(async delegate
            {
                await this.asyncPump.SwitchToMainThreadAsync();
                await firstYield;
                startingJoin.Set();
                joinable.Join();
            });

            Dispatcher.PushFrame(frame);
        }

        [StaFact]
        public void RunSynchronouslyWithoutSyncContext()
        {
            SynchronizationContext.SetSynchronizationContext(null);
            this.context = new JoinableTaskContext();
            this.joinableCollection = this.context.CreateCollection();
            this.asyncPump = this.context.CreateFactory(this.joinableCollection);
            this.asyncPump.Run(async delegate
            {
                await Task.Yield();
            });
        }

        /// <summary>
        /// Verifies the fix for a bug found in actual Visual Studio use of the AsyncPump.
        /// </summary>
        [StaFact]
        public void AsyncPumpEnumeratingModifiedCollection()
        {
            // Arrange for a pending action on this.asyncPump.
            var messagePosted = new AsyncManualResetEvent();
            var uiThreadReachedTask = Task.Run(async delegate
            {
                await this.asyncPump.SwitchToMainThreadAsync().GetAwaiter().YieldAndNotify(messagePosted);
            });

            // The repro in VS wasn't as concise (or possibly as contrived looking) as this.
            // This code sets up the minimal scenario for reproducing the bug that came about
            // through interactions of various CPS/VC components.
            var otherCollection = this.context.CreateCollection();
            var otherPump = this.context.CreateFactory(otherCollection);
            otherPump.Run(async delegate
            {
                await this.asyncPump.RunAsync(delegate
                {
                    return Task.Run(async delegate
                    {
                        await messagePosted; // wait for this.asyncPump.pendingActions to be non empty
                        using (var j = this.joinableCollection.Join())
                        {
                            await uiThreadReachedTask;
                        }
                    });
                });
            });
        }

        [StaFact]
        public void NoPostedMessageLost()
        {
            Assert.True(Task.Run(async delegate
            {
                var delegateExecuted = new AsyncManualResetEvent();
                SynchronizationContext syncContext = null;
                this.asyncPump.Run(delegate
                {
                    syncContext = SynchronizationContext.Current;
                    return TplExtensions.CompletedTask;
                });
                syncContext.Post(
                    delegate
                    {
                        delegateExecuted.Set();
                    },
                    null);
                await delegateExecuted;
            }).Wait(TestTimeout), "Timed out waiting for completion.");
        }

        [StaFact]
        public void NestedSyncContextsAvoidDeadlocks()
        {
            this.asyncPump.Run(async delegate
            {
                await this.asyncPump.RunAsync(async delegate
                {
                    await Task.Yield();
                });
            });
        }

        /// <summary>
        /// Verifies when JoinableTasks are nested that all factories' policies are involved
        /// in trying to get to the UI thread.
        /// </summary>
        [StaFact]
        public void NestedFactoriesCombinedMainThreadPolicies()
        {
            var loPriFactory = new ModalPumpingJoinableTaskFactory(this.context);
            var hiPriFactory = new ModalPumpingJoinableTaskFactory(this.context);

            var outer = hiPriFactory.RunAsync(async delegate
            {
                await loPriFactory.RunAsync(async delegate
                {
                    await Task.Yield();
                });
            });

            // Verify that the loPriFactory received the message.
            Assert.Equal(1, loPriFactory.JoinableTasksPendingMainthread.Count());

            // Simulate a modal dialog, with a message pump that is willing
            // to execute hiPriFactory messages but not loPriFactory messages.
            hiPriFactory.DoModalLoopTillEmpty();
            Assert.True(outer.IsCompleted);
        }

        /// <summary>
        /// Verifies when JoinableTasks are nested that nesting (parent) factories
        /// do not assist in reaching the main thread if the nesting JoinableTask
        /// completed before the child JoinableTask even started.
        /// </summary>
        /// <remarks>
        /// This is for efficiency as well as an accuracy assistance since the nested JTF
        /// may have a lower priority to get to the main thread (e.g. idle priority) than the
        /// parent JTF. If the parent JTF assists just because it happened to be active for a
        /// brief time when the child JoinableTask was created, it could forever defeat the
        /// intended lower priority of the child.
        /// </remarks>
        [StaFact]
        public void NestedFactoriesDoNotAssistChildrenOfTaskThatCompletedBeforeStart()
        {
            var loPriFactory = new ModalPumpingJoinableTaskFactory(this.context);
            var hiPriFactory = new ModalPumpingJoinableTaskFactory(this.context);

            var outerFinished = new AsyncManualResetEvent(allowInliningAwaiters: true);
            JoinableTask innerTask;
            AsyncManualResetEvent loPriSwitchPosted = new AsyncManualResetEvent();
            var outer = hiPriFactory.RunAsync(delegate
            {
                Task.Run(async delegate
                {
                    await outerFinished;
                    innerTask = loPriFactory.RunAsync(async delegate
                    {
                        await loPriFactory.SwitchToMainThreadAsync().GetAwaiter().YieldAndNotify(loPriSwitchPosted);
                    });
                });
                return TplExtensions.CompletedTask;
            });
            outerFinished.Set();
            loPriSwitchPosted.WaitAsync().Wait();

            // Verify that the loPriFactory received the message and hiPriFactory did not.
            Assert.Equal(1, loPriFactory.JoinableTasksPendingMainthread.Count());
            Assert.Equal(0, hiPriFactory.JoinableTasksPendingMainthread.Count());
        }

        /// <summary>
        /// Verifies when JoinableTasks are nested that nesting (parent) factories
        /// do not assist in reaching the main thread once the nesting JoinableTask
        /// completes (assuming it completes after the child JoinableTask starts).
        /// </summary>
        /// <remarks>
        /// This is for efficiency as well as an accuracy assistance since the nested JTF
        /// may have a lower priority to get to the main thread (e.g. idle priority) than the
        /// parent JTF. If the parent JTF assists just because it happened to be active for a
        /// brief time when the child JoinableTask was created, it could forever defeat the
        /// intended lower priority of the child.
        ///
        /// This test is Ignored because fixing it would require a JoinableTask to have
        /// a reference to its antecedant, or the antecedant to maintain a collection of
        /// child tasks. The first possibility is unpaletable (because it would create a
        /// memory leak for those who chain tasks together). The second one we sort of already
        /// do through the JoinableTask.childOrJoinedJobs field, and we may wire it up through
        /// there in the future.
        /// </remarks>
        [StaFact(Skip = "Ignored")]
        public void NestedFactoriesDoNotAssistChildrenOfTaskThatCompletedAfterStart()
        {
            var loPriFactory = new ModalPumpingJoinableTaskFactory(this.context);
            var hiPriFactory = new ModalPumpingJoinableTaskFactory(this.context);

            var outerFinished = new AsyncManualResetEvent(allowInliningAwaiters: true);
            JoinableTask innerTask;
            var outer = hiPriFactory.RunAsync(delegate
            {
                innerTask = loPriFactory.RunAsync(async delegate
                {
                    await outerFinished;
                });
                return TplExtensions.CompletedTask;
            });
            outerFinished.Set();

            // Verify that the loPriFactory received the message and hiPriFactory did not.
            Assert.Equal(1, loPriFactory.JoinableTasksPendingMainthread.Count());
            Assert.Equal(0, hiPriFactory.JoinableTasksPendingMainthread.Count());
        }

        /// <summary>
        /// Verifes that each instance of JTF is only notified once of
        /// a nested JoinableTask's attempt to get to the UI thread.
        /// </summary>
        [StaFact]
        public void NestedFactoriesDoNotDuplicateEffort()
        {
            var loPriFactory = new ModalPumpingJoinableTaskFactory(this.context);
            var hiPriFactory = new ModalPumpingJoinableTaskFactory(this.context);

            // For this test, we intentionally use each factory twice in a row.
            // We mix up the order in another test.
            var outer = hiPriFactory.RunAsync(async delegate
            {
                await hiPriFactory.RunAsync(async delegate
                {
                    await loPriFactory.RunAsync(async delegate
                    {
                        await loPriFactory.RunAsync(async delegate
                        {
                            await Task.Yield();
                        });
                    });
                });
            });

            // Verify that each factory received the message exactly once.
            Assert.Equal(1, loPriFactory.JoinableTasksPendingMainthread.Count());
            Assert.Equal(1, hiPriFactory.JoinableTasksPendingMainthread.Count());

            // Simulate a modal dialog, with a message pump that is willing
            // to execute hiPriFactory messages but not loPriFactory messages.
            hiPriFactory.DoModalLoopTillEmpty();
            Assert.True(outer.IsCompleted);
        }

        /// <summary>
        /// Verifes that each instance of JTF is only notified once of
        /// a nested JoinableTask's attempt to get to the UI thread.
        /// </summary>
        [StaFact]
        public void NestedFactoriesDoNotDuplicateEffortMixed()
        {
            var loPriFactory = new ModalPumpingJoinableTaskFactory(this.context);
            var hiPriFactory = new ModalPumpingJoinableTaskFactory(this.context);

            // In this particular test, we intentionally mix up the JTFs in hi-lo-hi-lo order.
            var outer = hiPriFactory.RunAsync(async delegate
            {
                await loPriFactory.RunAsync(async delegate
                {
                    await hiPriFactory.RunAsync(async delegate
                    {
                        await loPriFactory.RunAsync(async delegate
                        {
                            await Task.Yield();
                        });
                    });
                });
            });

            // Verify that each factory received the message exactly once.
            Assert.Equal(1, loPriFactory.JoinableTasksPendingMainthread.Count());
            Assert.Equal(1, hiPriFactory.JoinableTasksPendingMainthread.Count());

            // Simulate a modal dialog, with a message pump that is willing
            // to execute hiPriFactory messages but not loPriFactory messages.
            hiPriFactory.DoModalLoopTillEmpty();
            Assert.True(outer.IsCompleted);
        }

        [SkippableFact]
        public void NestedFactoriesCanBeCollected()
        {
            var outerFactory = new ModalPumpingJoinableTaskFactory(this.context);
            var innerFactory = new ModalPumpingJoinableTaskFactory(this.context);

            JoinableTask inner = null;
            var outer = outerFactory.RunAsync(async delegate
            {
                inner = innerFactory.RunAsync(async delegate
                {
                    await Task.Yield();
                });
                await inner;
            });

            outerFactory.DoModalLoopTillEmpty();
            Skip.IfNot(!outer.IsCompleted, "this is a product defect, but this test assumes this works to test something else.");

            // Allow the dispatcher to drain all messages that may be holding references.
            var frame = new DispatcherFrame();
            SynchronizationContext.Current.Post(s => frame.Continue = false, null);
            Dispatcher.PushFrame(frame);

            // Now we verify that while 'inner' is non-null that it doesn't hold outerFactory in memory
            // once 'inner' has completed.
            var weakOuterFactory = new WeakReference(outerFactory);
            outer = null;
            outerFactory = null;
            GC.Collect();
            Assert.False(weakOuterFactory.IsAlive);
        }

        // This is a known issue and we haven't a fix yet
        [StaFact(Skip = "Ignored")]
        public void CallContextWasOverwrittenByReentrance()
        {
            var asyncLock = new AsyncReaderWriterLock();

            // 4. This is the task which the UI thread is waiting for,
            //    and it's scheduled on UI thread.
            //    As UI thread did "Join" before "await", so this task can reenter UI thread.
            var task = Task.Run(async delegate
            {
                await this.asyncPump.SwitchToMainThreadAsync();

                // 4.1 Now this anonymous method is on UI thread,
                //     and it needs to acquire a read lock.
                //
                //     The attempt to acquire a lock would lead to a deadlock!
                //     Because the call context was overwritten by this reentrance,
                //     this method didn't know the write lock was already acquired at
                //     the bottom of the call stack. Therefore, it will issue a new request
                //     to acquire the read lock. However, that request won't be completed as
                //     the write lock holder is also waiting for this method to complete.
                //
                //     This test would be timeout here.
                using (await asyncLock.ReadLockAsync())
                {
                }
            });

            this.asyncPump.Run(async delegate
            {
                // 1. Acquire write lock on worker thread
                using (await asyncLock.WriteLockAsync())
                {
                    // 2. Hold the write lock but switch to UI thread.
                    //    That's to simulate the scenario to call into IVs* services
                    await this.asyncPump.SwitchToMainThreadAsync();

                    // 3. Join and wait for another BG task.
                    //    That's to simulate the scenario when the IVs* service also calls into CPS,
                    //    and CPS join and wait for another task.
                    using (this.joinableCollection.Join())
                    {
                        await task;
                    }
                }
            });
        }

        /// <summary>
        /// Rapidly posts messages to several interlinked AsyncPumps
        /// to check for thread-safety and deadlocks.
        /// </summary>
        [StaFact]
        public void PostStress()
        {
            int outstandingMessages = 0;
            var cts = new CancellationTokenSource(1000);
            var collection2 = this.asyncPump.Context.CreateCollection();
            var pump2 = this.asyncPump.Context.CreateFactory(collection2);
            Task t1 = null, t2 = null;
            var frame = new DispatcherFrame();

            ((DerivedJoinableTaskFactory)this.asyncPump).AssumeConcurrentUse = true;
            ((DerivedJoinableTaskFactory)pump2).AssumeConcurrentUse = true;

            pump2.Run(delegate
            {
                t1 = Task.Run(delegate
                {
                    using (this.joinableCollection.Join())
                    {
                        while (!cts.IsCancellationRequested)
                        {
                            var awaiter = pump2.SwitchToMainThreadAsync().GetAwaiter();
                            Interlocked.Increment(ref outstandingMessages);
                            awaiter.OnCompleted(delegate
                            {
                                awaiter.GetResult();
                                if (Interlocked.Decrement(ref outstandingMessages) == 0)
                                {
                                    frame.Continue = false;
                                }
                            });
                        }
                    }
                });
                return TplExtensions.CompletedTask;
            });

            this.asyncPump.Run(delegate
            {
                t2 = Task.Run(delegate
                {
                    using (collection2.Join())
                    {
                        while (!cts.IsCancellationRequested)
                        {
                            var awaiter = this.asyncPump.SwitchToMainThreadAsync().GetAwaiter();
                            Interlocked.Increment(ref outstandingMessages);
                            awaiter.OnCompleted(delegate
                            {
                                awaiter.GetResult();
                                if (Interlocked.Decrement(ref outstandingMessages) == 0)
                                {
                                    frame.Continue = false;
                                }
                            });
                        }
                    }
                });
                return TplExtensions.CompletedTask;
            });

            Dispatcher.PushFrame(frame);
        }

        /// <summary>
        /// Verifies that in the scenario when the initializing thread doesn't have a sync context at all (vcupgrade.exe)
        /// that reasonable behavior still occurs.
        /// </summary>
        [StaFact]
        public void NoMainThreadSyncContextAndKickedOffFromOriginalThread()
        {
            SynchronizationContext.SetSynchronizationContext(null);
            var context = new DerivedJoinableTaskContext();
            this.joinableCollection = context.CreateCollection();
            this.asyncPump = context.CreateFactory(this.joinableCollection);

            this.asyncPump.Run(async delegate
            {
                Assert.Same(this.originalThread, Thread.CurrentThread);
                await Task.Yield();

                await this.asyncPump.SwitchToMainThreadAsync();
                await Task.Yield();

                await TaskScheduler.Default;
                await Task.Yield();

                await this.asyncPump.SwitchToMainThreadAsync();
                await Task.Yield();
            });

            var joinable = this.asyncPump.RunAsync(async delegate
            {
                Assert.Same(this.originalThread, Thread.CurrentThread);
                await Task.Yield();

                // verifies no yield
                Assert.True(this.asyncPump.SwitchToMainThreadAsync().GetAwaiter().IsCompleted);

                await this.asyncPump.SwitchToMainThreadAsync();
                await Task.Yield();

                await TaskScheduler.Default;
                await Task.Yield();

                await this.asyncPump.SwitchToMainThreadAsync();
                await Task.Yield();
            });
            joinable.Join();
        }

        /// <summary>
        /// Verifies that in the scenario when the initializing thread doesn't have a sync context at all (vcupgrade.exe)
        /// that reasonable behavior still occurs.
        /// </summary>
        [StaFact]
        public void NoMainThreadSyncContextAndKickedOffFromOtherThread()
        {
            SynchronizationContext.SetSynchronizationContext(null);
            this.context = new DerivedJoinableTaskContext();
            this.joinableCollection = this.context.CreateCollection();
            this.asyncPump = this.context.CreateFactory(this.joinableCollection);
            Thread otherThread = null;

            Task.Run(delegate
            {
                otherThread = Thread.CurrentThread;
                this.asyncPump.Run(async delegate
                {
                    Assert.Same(otherThread, Thread.CurrentThread);
                    await Task.Yield();
                    Assert.Same(otherThread, Thread.CurrentThread);

                    // verifies no yield
                    Assert.True(this.asyncPump.SwitchToMainThreadAsync().GetAwaiter().IsCompleted);

                    await this.asyncPump.SwitchToMainThreadAsync(); // we expect this to no-op
                    Assert.Same(otherThread, Thread.CurrentThread);
                    await Task.Yield();
                    Assert.Same(otherThread, Thread.CurrentThread);

                    await Task.Run(async delegate
                    {
                        Thread threadpoolThread = Thread.CurrentThread;
                        Assert.NotSame(otherThread, Thread.CurrentThread);
                        await Task.Yield();
                        Assert.NotSame(otherThread, Thread.CurrentThread);

                        await this.asyncPump.SwitchToMainThreadAsync();
                        await Task.Yield();
                    });
                });

                var joinable = this.asyncPump.RunAsync(async delegate
                {
                    Assert.Same(otherThread, Thread.CurrentThread);
                    await Task.Yield();

                    // verifies no yield
                    Assert.True(this.asyncPump.SwitchToMainThreadAsync().GetAwaiter().IsCompleted);

                    await this.asyncPump.SwitchToMainThreadAsync(); // we expect this to no-op
                    await Task.Yield();

                    await Task.Run(async delegate
                    {
                        Thread threadpoolThread = Thread.CurrentThread;
                        await Task.Yield();

                        await this.asyncPump.SwitchToMainThreadAsync();
                        await Task.Yield();
                    });
                });
                joinable.Join();
            }).Wait();
        }

        [StaFact]
        public void MitigationAgainstBadSyncContextOnMainThread()
        {
            var ordinarySyncContext = new SynchronizationContext();
            SynchronizationContext.SetSynchronizationContext(ordinarySyncContext);
            var assertDialogListener = Trace.Listeners.OfType<DefaultTraceListener>().FirstOrDefault();
            assertDialogListener.AssertUiEnabled = false;
            this.asyncPump.Run(async delegate
            {
                await Task.Yield();
                await this.asyncPump.SwitchToMainThreadAsync();
            });
            assertDialogListener.AssertUiEnabled = true;
        }

        [StaFact, Trait("Stress", "true"), Trait("TestCategory", "FailsInCloudTest"), Trait("FailsInLocalBatch", "true")]
        public void SwitchToMainThreadMemoryLeak()
        {
            this.CheckGCPressure(
                async delegate
                {
                    await TaskScheduler.Default;
                    await this.asyncPump.SwitchToMainThreadAsync(CancellationToken.None);
                },
                2500);
        }

        [StaFact, Trait("Stress", "true"), Trait("TestCategory", "FailsInCloudTest"), Trait("FailsInLocalBatch", "true")]
        public void SwitchToMainThreadMemoryLeakWithCancellationToken()
        {
            CancellationTokenSource tokenSource = new CancellationTokenSource();
            this.CheckGCPressure(
                async delegate
                {
                    await TaskScheduler.Default;
                    await this.asyncPump.SwitchToMainThreadAsync(tokenSource.Token);
                },
                2500);
        }

        [StaFact]
        public void SwitchToMainThreadSucceedsWhenConstructedUnderMTAOperation()
        {
            var frame = new DispatcherFrame();
            var task = Task.Run(async delegate
            {
                try
                {
                    var otherCollection = this.context.CreateCollection();
                    var otherPump = this.context.CreateFactory(otherCollection);
                    await otherPump.SwitchToMainThreadAsync();
                    Assert.Same(this.originalThread, Thread.CurrentThread);
                }
                finally
                {
                    frame.Continue = false;
                }
            });

            Dispatcher.PushFrame(frame);
            task.GetAwaiter().GetResult(); // rethrow any failures
        }

        [StaFact, Trait("GC", "true")]
        public void JoinableTaskReleasedBySyncContextAfterCompletion()
        {
            SynchronizationContext syncContext = null;
            var job = new WeakReference(this.asyncPump.RunAsync(() =>
            {
                syncContext = SynchronizationContext.Current; // simulate someone who has captured the sync context.
                return TplExtensions.CompletedTask;
            }));

            // We intentionally still have a reference to the SyncContext that represents the task.
            // We want to make sure that even with that, the JoinableTask itself can be collected.
            GC.Collect();
            Assert.False(job.IsAlive);
        }

        [StaFact]
        public void JoinTwice()
        {
            var joinable = this.asyncPump.RunAsync(async delegate
            {
                await Task.Yield();
            });

            this.asyncPump.Run(async delegate
            {
                var task1 = joinable.JoinAsync();
                var task2 = joinable.JoinAsync();
                await Task.WhenAll(task1, task2);
            });
        }

        [StaFact]
        public void GrandparentJoins()
        {
            var innerJoinable = this.asyncPump.RunAsync(async delegate
            {
                await Task.Yield();
            });

            var outerJoinable = this.asyncPump.RunAsync(async delegate
            {
                await innerJoinable;
            });

            outerJoinable.Join();
        }

        [StaFact, Trait("GC", "true"), Trait("TestCategory", "FailsInCloudTest")]
        public void RunSynchronouslyTaskNoYieldGCPressure()
        {
            this.CheckGCPressure(delegate
            {
                this.asyncPump.Run(delegate
                {
                    return TplExtensions.CompletedTask;
                });
            }, maxBytesAllocated: 500);
        }

        [StaFact, Trait("GC", "true"), Trait("TestCategory", "FailsInCloudTest")]
        public void RunSynchronouslyTaskOfTNoYieldGCPressure()
        {
            Task<object> completedTask = Task.FromResult<object>(null);

            this.CheckGCPressure(delegate
            {
                this.asyncPump.Run(delegate
                {
                    return completedTask;
                });
            }, maxBytesAllocated: 500);
        }

        [StaFact, Trait("GC", "true"), Trait("TestCategory", "FailsInCloudTest"), Trait("FailsInLocalBatch", "true")]
        public void RunSynchronouslyTaskWithYieldGCPressure()
        {
            this.CheckGCPressure(delegate
            {
                this.asyncPump.Run(async delegate
                {
                    await Task.Yield();
                });
            }, maxBytesAllocated: 1800);
        }

        [StaFact, Trait("GC", "true"), Trait("TestCategory", "FailsInCloudTest"), Trait("FailsInLocalBatch", "true")]
        public void RunSynchronouslyTaskOfTWithYieldGCPressure()
        {
            Task<object> completedTask = Task.FromResult<object>(null);

            this.CheckGCPressure(delegate
            {
                this.asyncPump.Run(async delegate
                {
                    await Task.Yield();
                });
            }, maxBytesAllocated: 1800);
        }

        /// <summary>
        /// Verifies that when two AsyncPumps are stacked on the main thread by (unrelated) COM reentrancy
        /// that the bottom one doesn't "steal" the work before the inner one can when the outer one
        /// isn't on the top of the stack and therefore can't execute it anyway, thereby precluding the
        /// inner one from executing it either and leading to deadlock.
        /// </summary>
        [StaFact]
        public void NestedRunSynchronouslyOuterDoesNotStealWorkFromNested()
        {
            var collection = this.context.CreateCollection();
            var asyncPump = new COMReentrantJoinableTaskFactory(collection);
            var nestedWorkBegun = new AsyncManualResetEvent();
            asyncPump.ReenterWaitWith(() =>
            {
                asyncPump.Run(async delegate
                {
                    await Task.Yield();
                });

                nestedWorkBegun.Set();
            });

            asyncPump.Run(async delegate
            {
                await nestedWorkBegun;
            });
        }

        [StaFact]
        public void RunAsyncExceptionsCapturedInResult()
        {
            var exception = new InvalidOperationException();
            var joinableTask = this.asyncPump.RunAsync(delegate
            {
                throw exception;
            });
            Assert.True(joinableTask.IsCompleted);
            Assert.Same(exception, joinableTask.Task.Exception.InnerException);
            var awaiter = joinableTask.GetAwaiter();
            try
            {
                awaiter.GetResult();
                Assert.True(false, "Expected exception not rethrown.");
            }
            catch (InvalidOperationException ex)
            {
                Assert.Same(ex, exception);
            }
        }

        [StaFact]
        public void RunAsyncOfTExceptionsCapturedInResult()
        {
            var exception = new InvalidOperationException();
            var joinableTask = this.asyncPump.RunAsync<int>(delegate
            {
                throw exception;
            });
            Assert.True(joinableTask.IsCompleted);
            Assert.Same(exception, joinableTask.Task.Exception.InnerException);
            var awaiter = joinableTask.GetAwaiter();
            try
            {
                awaiter.GetResult();
                Assert.True(false, "Expected exception not rethrown.");
            }
            catch (InvalidOperationException ex)
            {
                Assert.Same(ex, exception);
            }
        }

        [StaFact]
        public void RunAsyncWithNonYieldingDelegateNestedInRunOverhead()
        {
            var waitCountingJTF = new WaitCountingJoinableTaskFactory(this.asyncPump.Context);
            waitCountingJTF.Run(async delegate
            {
                await TaskScheduler.Default;
                for (int i = 0; i < 1000; i++)
                {
                    // TIP: spinning gives the blocking thread longer to wake up, which
                    // if it wakes up at all tends to mean it will wake up in time for more
                    // of the iterations, showing that doing real work exercerbates the problem.
                    ////for (int j = 0; j < 5000; j++) { }

                    await this.asyncPump.RunAsync(delegate { return TplExtensions.CompletedTask; });
                }
            });

            // We assert that since the blocking thread didn't need to wake up at all,
            // it should have only slept once. Any more than that constitutes unnecessary overhead.
            Assert.Equal(1, waitCountingJTF.WaitCount);
        }

        [StaFact]
        public void RunAsyncWithYieldingDelegateNestedInRunOverhead()
        {
            var waitCountingJTF = new WaitCountingJoinableTaskFactory(this.asyncPump.Context);
            waitCountingJTF.Run(async delegate
            {
                await TaskScheduler.Default;
                for (int i = 0; i < 1000; i++)
                {
                    // TIP: spinning gives the blocking thread longer to wake up, which
                    // if it wakes up at all tends to mean it will wake up in time for more
                    // of the iterations, showing that doing real work exercerbates the problem.
                    ////for (int j = 0; j < 5000; j++) { }

                    await this.asyncPump.RunAsync(async delegate { await Task.Yield(); });
                }
            });

            // We assert that since the blocking thread didn't need to wake up at all,
            // it should have only slept once. Any more than that constitutes unnecessary overhead.
            Assert.Equal(1, waitCountingJTF.WaitCount);
        }

        [StaFact]
        public void SwitchToMainThreadShouldNotLeakJoinableTaskWhenGetResultRunsFirst()
        {
            var cts = new CancellationTokenSource();
            var factory = (DerivedJoinableTaskFactory)this.asyncPump;
            var transitionedToMainThread = new ManualResetEventSlim(false);
            factory.PostToUnderlyingSynchronizationContextCallback = () =>
            {
                // Pause the background thread after posted the continuation to JoinableTask.
                transitionedToMainThread.Wait();
            };

            object result = new object();
            WeakReference<object> weakResult = new WeakReference<object>(result);

            this.asyncPump.Run(async () =>
            {
                // Needs to switch to background thread at first in order to test the code that requests switch to main thread.
                await TaskScheduler.Default;

                // This nested run starts on background thread and then requests to switch to main thread.
                // The remaining parts in the async delegate would be executed on main thread. This nested run
                // will complete only when both the background thread works (aka. MainThreadAWaiter.OnCompleted())
                // and the main thread works are done, and then we could start verification.
                this.asyncPump.Run(async () =>
            {
                await this.asyncPump.SwitchToMainThreadAsync(cts.Token);

                // Resume the background thread after transitioned to main thread.
                // This is to ensure the timing that GetResult() must be called before OnCompleted() registers the cancellation.
                transitionedToMainThread.Set();
                return result;
            });
            });

            // Needs to give the dispatcher a chance to run the posted action in order to release
            // the last reference to the JoinableTask.
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke((Action)(() =>
            {
                frame.Continue = false;
            }));
            Dispatcher.PushFrame(frame);

            result = null;
            GC.Collect();

            object target;
            weakResult.TryGetTarget(out target);
            Assert.Null(target); //, "The task's result should be collected unless the JoinableTask is leaked");
        }

        [StaFact]
        public void SwitchToMainThreadShouldNotLeakJoinableTaskWhenGetResultRunsLater()
        {
            var cts = new CancellationTokenSource();
            var factory = (DerivedJoinableTaskFactory)this.asyncPump;
            var waitForOnCompletedIsFinished = new ManualResetEventSlim(false);
            factory.TransitionedToMainThreadCallback = (jt) =>
            {
                // Pause the main thread before execute the continuation.
                waitForOnCompletedIsFinished.Wait();
            };

            object result = new object();
            WeakReference<object> weakResult = new WeakReference<object>(result);

            this.asyncPump.Run(async () =>
            {
                // Needs to switch to background thread at first in order to test the code that requests switch to main thread.
                await TaskScheduler.Default;

                // This nested async run starts on background thread and then requests to switch to main thread.
                // It will complete only when the background thread works (aka. MainThreadAWaiter.OnCompleted()) are done,
                // and then we will signal a test event to resume the main thread execution, to let the remaining parts
                // in the async delegate go through.
                var joinable = this.asyncPump.RunAsync(async () =>
            {
                await this.asyncPump.SwitchToMainThreadAsync(cts.Token);
                return result;
            });

                // Resume the main thread after OnCompleted() finishes.
                // This is to ensure the timing that GetResult() must be called after OnCompleted() is fully done.
                waitForOnCompletedIsFinished.Set();
                await joinable;
            });

            // Needs to give the dispatcher a chance to run the posted action in order to release
            // the last reference to the JoinableTask.
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke((Action)(() =>
            {
                frame.Continue = false;
            }));
            Dispatcher.PushFrame(frame);

            result = null;
            GC.Collect();

            object target;
            weakResult.TryGetTarget(out target);
            Assert.Null(target); // The task's result should be collected unless the JoinableTask is leaked
        }

        [StaFact]
        public void PostToUnderlyingSynchronizationContextShouldBeAfterSignalJoinableTasks()
        {
            var factory = (DerivedJoinableTaskFactory)this.asyncPump;
            var transitionedToMainThread = new ManualResetEventSlim(false);
            factory.PostToUnderlyingSynchronizationContextCallback = () =>
            {
                // The JoinableTask should be wakened up and the code to set this event should be executed on main thread,
                // otherwise, this wait will cause test timeout.
                transitionedToMainThread.Wait();
            };
            this.asyncPump.Run(async delegate
            {
                await TaskScheduler.Default;
                await this.asyncPump.SwitchToMainThreadAsync();
                transitionedToMainThread.Set();
            });
        }

        protected override JoinableTaskContext CreateJoinableTaskContext()
        {
            return new DerivedJoinableTaskContext();
        }

        private static async void SomeFireAndForgetMethod()
        {
            await Task.Yield();
        }

        private async Task SomeOperationThatMayBeOnMainThreadAsync()
        {
            await Task.Yield();
            await Task.Yield();
        }

        private Task SomeOperationThatUsesMainThreadViaItsOwnAsyncPumpAsync()
        {
            var otherCollection = this.context.CreateCollection();
            var privateAsyncPump = this.context.CreateFactory(otherCollection);
            return Task.Run(async delegate
            {
                await Task.Yield();
                await privateAsyncPump.SwitchToMainThreadAsync();
                await Task.Yield();
            });
        }

        private async Task TestReentrancyOfUnrelatedDependentWork()
        {
            var unrelatedMainThreadWorkWaiting = new AsyncManualResetEvent();
            var unrelatedMainThreadWorkInvoked = new AsyncManualResetEvent();
            JoinableTaskCollection unrelatedCollection;
            JoinableTaskFactory unrelatedPump;
            Task unrelatedTask;

            // don't let this task be identified as related to the caller, so that the caller has to Join for this to complete.
            using (this.context.SuppressRelevance())
            {
                unrelatedCollection = this.context.CreateCollection();
                unrelatedPump = this.context.CreateFactory(unrelatedCollection);
                unrelatedTask = Task.Run(async delegate
                {
                    await unrelatedPump.SwitchToMainThreadAsync().GetAwaiter().YieldAndNotify(unrelatedMainThreadWorkWaiting, unrelatedMainThreadWorkInvoked);
                    Assert.Same(this.originalThread, Thread.CurrentThread);
                });
            }

            await unrelatedMainThreadWorkWaiting.WaitAsync();

            // Await an extra bit of time to allow for unexpected reentrancy to occur while the
            // main thread is only synchronously blocking.
            var waitTask = unrelatedMainThreadWorkInvoked.WaitAsync();
            Assert.NotSame(
                waitTask,
                await Task.WhenAny(waitTask, Task.Delay(AsyncDelay / 2))); // Background work completed work on the UI thread before it was invited to do so.

            using (unrelatedCollection.Join())
            {
                // The work SHOULD be able to complete now that we've Joined the work.
                await waitTask;
                Assert.Same(this.originalThread, Thread.CurrentThread);
            }
        }

        private void RunFuncOfTaskHelper()
        {
            var initialThread = Thread.CurrentThread;
            this.asyncPump.Run(async delegate
            {
                Assert.Same(initialThread, Thread.CurrentThread);
                await Task.Yield();
                Assert.Same(initialThread, Thread.CurrentThread);
            });
        }

        private void RunFuncOfTaskOfTHelper()
        {
            var initialThread = Thread.CurrentThread;
            var expectedResult = new GenericParameterHelper();
            GenericParameterHelper actualResult = this.asyncPump.Run(async delegate
            {
                Assert.Same(initialThread, Thread.CurrentThread);
                await Task.Yield();
                Assert.Same(initialThread, Thread.CurrentThread);
                return expectedResult;
            });
            Assert.Same(expectedResult, actualResult);
        }

        /// <summary>
        /// Writes out a DGML graph of pending tasks and collections to the test context.
        /// </summary>
        /// <param name="context">A specific context to collect data from; <c>null</c> will use this.context</param>
        private void PrintActiveTasksReport(JoinableTaskContext context = null)
        {
            context = context ?? this.context;
            IHangReportContributor contributor = context;
            var report = contributor.GetHangReport();
            this.Logger.WriteLine("DGML task graph");
            this.Logger.WriteLine(report.Content);
        }

        /// <summary>
        /// Simulates COM message pump reentrancy causing some unrelated work to "pump in" on top of a synchronously blocking wait.
        /// </summary>
        private class COMReentrantJoinableTaskFactory : JoinableTaskFactory
        {
            private Action action;

            internal COMReentrantJoinableTaskFactory(JoinableTaskContext context)
                : base(context)
            {
            }

            internal COMReentrantJoinableTaskFactory(JoinableTaskCollection collection)
                : base(collection)
            {
            }

            internal void ReenterWaitWith(Action action)
            {
                this.action = action;
            }

            protected override void WaitSynchronously(Task task)
            {
                if (this.action != null)
                {
                    var action = this.action;
                    this.action = null;
                    action();
                }

                base.WaitSynchronously(task);
            }
        }

        private class WaitCountingJoinableTaskFactory : JoinableTaskFactory
        {
            private int waitCount;

            internal WaitCountingJoinableTaskFactory(JoinableTaskContext owner)
                : base(owner)
            {
            }

            internal int WaitCount
            {
                get { return this.waitCount; }
            }

            protected override void WaitSynchronously(Task task)
            {
                Interlocked.Increment(ref this.waitCount);
                base.WaitSynchronously(task);
            }
        }

        private class DerivedJoinableTaskContext : JoinableTaskContext
        {
            public override JoinableTaskFactory CreateFactory(JoinableTaskCollection collection)
            {
                return new DerivedJoinableTaskFactory(collection);
            }
        }

        private class DerivedJoinableTaskFactory : JoinableTaskFactory
        {
            private readonly HashSet<JoinableTask> transitioningTasks = new HashSet<JoinableTask>();
            private int transitioningToMainThreadHitCount;
            private int transitionedToMainThreadHitCount;

            private JoinableTaskCollection transitioningTasksCollection;

            internal DerivedJoinableTaskFactory(JoinableTaskContext context)
                : base(context)
            {
                this.transitioningTasksCollection = new JoinableTaskCollection(context, refCountAddedJobs: true);
            }

            internal DerivedJoinableTaskFactory(JoinableTaskCollection collection)
                : base(collection)
            {
                this.transitioningTasksCollection = new JoinableTaskCollection(collection.Context, refCountAddedJobs: true);
            }

            internal int TransitionedToMainThreadHitCount
            {
                get { return this.transitionedToMainThreadHitCount; }
            }

            internal int TransitioningToMainThreadHitCount
            {
                get { return this.transitioningToMainThreadHitCount; }
            }

            internal bool AssumeConcurrentUse { get; set; }

            internal int TransitioningTasksCount
            {
                get { return this.transitioningTasksCollection.Count(); }
            }

            internal Action<JoinableTask> TransitioningToMainThreadCallback { get; set; }

            internal Action<JoinableTask> TransitionedToMainThreadCallback { get; set; }

            internal Action PostToUnderlyingSynchronizationContextCallback { get; set; }

            protected override void OnTransitioningToMainThread(JoinableTask joinableTask)
            {
                base.OnTransitioningToMainThread(joinableTask);
                Assert.False(joinableTask.IsCompleted, "A task should not be completed until at least the transition has completed.");
                Interlocked.Increment(ref this.transitioningToMainThreadHitCount);

                this.transitioningTasksCollection.Add(joinableTask);

                // These statements and assertions assume that the test does not have jobs that execute code concurrently.
                lock (this.transitioningTasks)
                {
                    Assert.True(this.transitioningTasks.Add(joinableTask) || this.AssumeConcurrentUse);
                }

                if (!this.AssumeConcurrentUse)
                {
                    Assert.Equal(this.TransitionedToMainThreadHitCount + 1, this.TransitioningToMainThreadHitCount); //, "Imbalance of transition events.");
                }

                if (this.TransitioningToMainThreadCallback != null)
                {
                    this.TransitioningToMainThreadCallback(joinableTask);
                }
            }

            protected override void OnTransitionedToMainThread(JoinableTask joinableTask, bool canceled)
            {
                base.OnTransitionedToMainThread(joinableTask, canceled);
                Interlocked.Increment(ref this.transitionedToMainThreadHitCount);

                this.transitioningTasksCollection.Remove(joinableTask);

                if (canceled)
                {
#if DESKTOP
                    Assert.NotSame(this.Context.MainThread, Thread.CurrentThread); // A canceled transition should not complete on the main thread.
#endif
                    Assert.False(this.Context.IsOnMainThread);
                }
                else
                {
#if DESKTOP
                    Assert.Same(this.Context.MainThread, Thread.CurrentThread); // We should be on the main thread if we've just transitioned.
#endif
                    Assert.True(this.Context.IsOnMainThread);
                }

                // These statements and assertions assume that the test does not have jobs that execute code concurrently.
                lock (this.transitioningTasks)
                {
                    Assert.True(this.transitioningTasks.Remove(joinableTask) || this.AssumeConcurrentUse);
                }

                if (!this.AssumeConcurrentUse)
                {
                    Assert.Equal(this.TransitionedToMainThreadHitCount, this.TransitioningToMainThreadHitCount); //, "Imbalance of transition events.");
                }

                if (this.TransitionedToMainThreadCallback != null)
                {
                    this.TransitionedToMainThreadCallback(joinableTask);
                }
            }

            protected override void WaitSynchronously(Task task)
            {
                Assert.NotNull(task);
                base.WaitSynchronously(task);
            }

            protected override void PostToUnderlyingSynchronizationContext(SendOrPostCallback callback, object state)
            {
                Assert.NotNull(this.UnderlyingSynchronizationContext);
                Assert.NotNull(callback);
                Assert.IsType(typeof(DispatcherSynchronizationContext), this.UnderlyingSynchronizationContext);
                base.PostToUnderlyingSynchronizationContext(callback, state);
                if (this.PostToUnderlyingSynchronizationContextCallback != null)
                {
                    this.PostToUnderlyingSynchronizationContextCallback();
                }
            }
        }

        private class ModalPumpingJoinableTaskFactory : JoinableTaskFactory
        {
            private ConcurrentQueue<Tuple<SendOrPostCallback, object>> queuedMessages = new ConcurrentQueue<Tuple<SendOrPostCallback, object>>();

            internal ModalPumpingJoinableTaskFactory(JoinableTaskContext context)
                : base(context)
            {
            }

            internal IEnumerable<Tuple<SendOrPostCallback, object>> JoinableTasksPendingMainthread
            {
                get { return this.queuedMessages; }
            }

            /// <summary>
            /// Executes all work posted to this factory.
            /// </summary>
            internal void DoModalLoopTillEmpty()
            {
                Tuple<SendOrPostCallback, object> work;
                while (this.queuedMessages.TryDequeue(out work))
                {
                    work.Item1(work.Item2);
                }
            }

            protected override void PostToUnderlyingSynchronizationContext(SendOrPostCallback callback, object state)
            {
                this.queuedMessages.Enqueue(Tuple.Create(callback, state));
                base.PostToUnderlyingSynchronizationContext(callback, state);
            }
        }

        private class MockAsyncService
        {
            private JoinableTaskCollection joinableCollection;
            private JoinableTaskFactory pump;
            private AsyncManualResetEvent stopRequested = new AsyncManualResetEvent();
            private Thread originalThread = Thread.CurrentThread;
            private Task dependentTask;
            private MockAsyncService dependentService;

            internal MockAsyncService(JoinableTaskContext context, MockAsyncService dependentService = null)
            {
                this.joinableCollection = context.CreateCollection();
                this.pump = context.CreateFactory(this.joinableCollection);
                this.dependentService = dependentService;
            }

            internal async Task OperationAsync()
            {
                await this.pump.SwitchToMainThreadAsync();
                if (this.dependentService != null)
                {
                    await (this.dependentTask = this.dependentService.OperationAsync());
                }

                await this.stopRequested.WaitAsync();
                await Task.Yield();
                Assert.Same(this.originalThread, Thread.CurrentThread);
            }

            internal async Task StopAsync(Task operation)
            {
                Requires.NotNull(operation, nameof(operation));
                if (this.dependentService != null)
                {
                    await this.dependentService.StopAsync(this.dependentTask);
                }

                this.stopRequested.Set();
                using (this.joinableCollection.Join())
                {
                    await operation;
                }
            }
        }
    }
}
