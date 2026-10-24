using MobiFlight.Base;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace MobiFlight.Execution.Tests
{
    [TestClass]
    public class RetriggerEventPacerTests
    {
        private DateTime _now;
        private List<InputEventArgs> _executed;
        private List<int> _delays;
        private int _delayMs;
        private Func<InputEventArgs, bool> _executeResult;

        [TestInitialize]
        public void SetUp()
        {
            _now = new DateTime(2026, 1, 1, 12, 0, 0);
            _executed = new List<InputEventArgs>();
            _delays = new List<int>();
            _delayMs = 0;
            _executeResult = e => true;
        }

        private RetriggerEventPacer CreatePacer(Func<int, CancellationToken, Task> delay = null)
        {
            return new RetriggerEventPacer(
                e => { lock (_executed) { _executed.Add(e); } return _executeResult(e); },
                () => _delayMs,
                () => _now,
                delay ?? ((ms, token) => { _delays.Add(ms); return Task.CompletedTask; }));
        }

        private static InputEventArgs CreateEvent(string name, DeviceType type = DeviceType.Button)
        {
            return new InputEventArgs
            {
                Controller = new Controller() { Name = "TestBoard", Serial = "SN-1" },
                Device = new DeviceReference() { Name = name },
                InputType = type,
                Value = (int)MobiFlightButton.InputEvent.RELEASE,
            };
        }

        #region Outside a retrigger

        [TestMethod]
        public void Handle_NoRetriggerInProgress_ExecutesImmediatelyWithoutTag()
        {
            // Arrange
            _delayMs = 20;
            var pacer = CreatePacer();
            var e = CreateEvent("Button1");

            // Act
            pacer.Handle(e);

            // Assert
            Assert.HasCount(1, _executed);
            Assert.IsFalse(e.IsRetrigger);
            Assert.IsEmpty(_delays);
        }

        [TestMethod]
        public void Handle_NullEvent_IsIgnored()
        {
            // Arrange
            var pacer = CreatePacer();

            // Act
            pacer.Handle(null);

            // Assert
            Assert.IsEmpty(_executed);
        }

        #endregion

        #region During a retrigger

        [TestMethod]
        public void Handle_DuringRetriggerWithoutDelay_ExecutesImmediatelyAndTagsEvent()
        {
            // Arrange
            var pacer = CreatePacer();
            var e = CreateEvent("Button1");
            pacer.BeginRetrigger();

            // Act
            pacer.Handle(e);

            // Assert
            Assert.HasCount(1, _executed);
            Assert.IsTrue(e.IsRetrigger);
            Assert.IsEmpty(_delays);
        }

        [TestMethod]
        public async Task Handle_DuringRetriggerWithDelay_ExecutesAllEventsInOrderWithDelay()
        {
            // Arrange
            _delayMs = 15;
            var pacer = CreatePacer();
            pacer.BeginRetrigger();

            // Act
            for (var i = 0; i < 5; i++)
            {
                pacer.Handle(CreateEvent($"Button{i}"));
            }
            await pacer.Worker;

            // Assert
            Assert.HasCount(5, _executed);
            for (var i = 0; i < 5; i++)
            {
                Assert.AreEqual($"Button{i}", _executed[i].Device.Name);
                Assert.IsTrue(_executed[i].IsRetrigger);
            }
            CollectionAssert.AreEqual(new[] { 15, 15, 15, 15, 15 }, _delays);
        }

        [TestMethod]
        public async Task Handle_DelayAboveMaximum_IsClamped()
        {
            // Arrange
            _delayMs = 60000;
            var pacer = CreatePacer();
            pacer.BeginRetrigger();

            // Act
            pacer.Handle(CreateEvent("Button1"));
            await pacer.Worker;

            // Assert
            Assert.HasCount(1, _delays);
            Assert.AreEqual(RetriggerEventPacer.MaxDelayMs, _delays[0]);
        }

        [TestMethod]
        public void Handle_EventWhileQueueIsBusy_IsQueuedBehindEarlierEvents()
        {
            // Arrange
            _delayMs = 10;
            var release = new TaskCompletionSource<bool>();
            var pacer = CreatePacer((ms, token) => release.Task);
            pacer.BeginRetrigger();
            pacer.Handle(CreateEvent("Button1"));
            SpinWait.SpinUntil(() => _executed.Count == 1, 2000);

            // Act
            pacer.Handle(CreateEvent("Button2"));

            // Assert
            Assert.HasCount(1, _executed, "Button2 must wait for the delay after Button1.");
            Assert.IsTrue(pacer.IsRetriggerActive);

            release.SetResult(true);
            Assert.IsTrue(pacer.Worker.Wait(2000));
            Assert.HasCount(2, _executed);
            Assert.AreEqual("Button2", _executed[1].Device.Name);
        }

        [TestMethod]
        public async Task Handle_EventsThatExecutedNothing_AreNotDelayed()
        {
            // Arrange
            _delayMs = 15;
            _executeResult = e => e.Device.Name != "Skipped";
            var pacer = CreatePacer();
            pacer.BeginRetrigger();

            // Act
            pacer.Handle(CreateEvent("Skipped"));
            pacer.Handle(CreateEvent("Button1"));
            pacer.Handle(CreateEvent("Skipped"));
            await pacer.Worker;

            // Assert
            Assert.HasCount(3, _executed);
            Assert.HasCount(1, _delays);
        }

        #endregion

        #region End of a retrigger

        [TestMethod]
        public void Handle_EventAfterWindowWhileQueueIsBusy_IsQueuedButNotTagged()
        {
            // Arrange
            _delayMs = 10;
            var release = new TaskCompletionSource<bool>();
            var pacer = CreatePacer((ms, token) => release.Task);
            pacer.BeginRetrigger();
            pacer.Handle(CreateEvent("Button1"));
            SpinWait.SpinUntil(() => _executed.Count == 1, 2000);
            _now = _now.AddMilliseconds(RetriggerEventPacer.MaxWindowMs + 1);
            var physical = CreateEvent("Button2");

            // Act
            pacer.Handle(physical);

            // Assert
            Assert.HasCount(1, _executed, "Button2 must wait behind the queued retrigger events.");
            Assert.IsFalse(physical.IsRetrigger, "Events after the window are physical, even while the queue drains.");

            release.SetResult(true);
            Assert.IsTrue(pacer.Worker.Wait(2000));
            Assert.HasCount(2, _executed);
            Assert.IsFalse(pacer.IsRetriggerActive);
        }

        [TestMethod]
        public void Handle_AfterQuietPeriod_ExecutesImmediatelyWithoutTag()
        {
            // Arrange
            var pacer = CreatePacer();
            pacer.BeginRetrigger();
            pacer.Handle(CreateEvent("Button1"));
            _now = _now.AddMilliseconds(RetriggerEventPacer.QuietPeriodMs + 1);
            var e = CreateEvent("Button2");

            // Act
            pacer.Handle(e);

            // Assert
            Assert.IsFalse(e.IsRetrigger);
            Assert.IsFalse(pacer.IsRetriggerActive);
        }

        [TestMethod]
        public void Handle_ContinuousButtonEvents_WindowEndsAfterMaximum()
        {
            // Arrange
            var pacer = CreatePacer();
            pacer.BeginRetrigger();
            var steps = RetriggerEventPacer.MaxWindowMs / 100;
            for (var i = 0; i < steps - 1; i++)
            {
                _now = _now.AddMilliseconds(100);
                pacer.Handle(CreateEvent($"Button{i}"));
            }
            _now = _now.AddMilliseconds(100);
            var e = CreateEvent("Late");

            // Act
            pacer.Handle(e);

            // Assert
            Assert.IsFalse(e.IsRetrigger);
        }

        [TestMethod]
        public void Handle_AnalogEvents_DoNotExtendWindow()
        {
            // Arrange
            var pacer = CreatePacer();
            pacer.BeginRetrigger();
            _now = _now.AddMilliseconds(RetriggerEventPacer.QuietPeriodMs - 100);
            var axis = CreateEvent("Axis1", DeviceType.AnalogInput);
            pacer.Handle(axis);
            _now = _now.AddMilliseconds(200);
            var e = CreateEvent("Button1");

            // Act
            pacer.Handle(e);

            // Assert
            Assert.IsTrue(axis.IsRetrigger, "Events inside the window are tagged regardless of their type.");
            Assert.IsFalse(e.IsRetrigger, "The axis event must not have extended the window.");
        }

        [TestMethod]
        public void Handle_NewRetrigger_OpensWindowAgain()
        {
            // Arrange
            var pacer = CreatePacer();
            pacer.BeginRetrigger();
            _now = _now.AddMilliseconds(RetriggerEventPacer.MaxWindowMs + 1);
            pacer.Handle(CreateEvent("Button1"));
            var e = CreateEvent("Button2");

            // Act
            pacer.BeginRetrigger();
            pacer.Handle(e);

            // Assert
            Assert.IsTrue(e.IsRetrigger);
        }

        #endregion

        #region Reset

        [TestMethod]
        public void Reset_DropsQueuedEventsAndEndsRetrigger()
        {
            // Arrange
            _delayMs = 10;
            var pacer = CreatePacer((ms, token) => Task.Delay(Timeout.Infinite, token));
            pacer.BeginRetrigger();
            pacer.Handle(CreateEvent("Button1"));
            pacer.Handle(CreateEvent("Button2"));
            pacer.Handle(CreateEvent("Button3"));
            SpinWait.SpinUntil(() => _executed.Count == 1, 2000);
            var worker = pacer.Worker;

            // Act
            pacer.Reset();

            // Assert
            Assert.IsTrue(worker.Wait(2000), "The worker must stop after Reset().");
            Assert.HasCount(1, _executed);
            Assert.IsFalse(pacer.IsRetriggerActive);

            var e = CreateEvent("Button4");
            pacer.Handle(e);
            Assert.HasCount(2, _executed);
            Assert.IsFalse(e.IsRetrigger);
        }

        #endregion
    }
}
