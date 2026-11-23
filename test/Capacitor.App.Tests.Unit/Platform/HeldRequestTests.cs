using Capacitor.App.Platform;

namespace Capacitor.App.Tests.Unit.Platform;

public class HeldRequestTests {
    [Test]
    public async Task A_request_raised_before_anyone_listens_reaches_the_first_listener_once() {
        var request = new HeldRequest();
        var first = 0;
        var second = 0;

        request.Raise();
        request.Raise();
        request.Listen(() => first++);
        request.Listen(() => second++);

        await Assert.That(first).IsEqualTo(1);
        await Assert.That(second).IsEqualTo(0);
    }

    [Test]
    public async Task A_request_raised_to_a_listener_is_not_held_for_the_next_one() {
        var request = new HeldRequest();
        var first = 0;
        var second = 0;

        request.Listen(() => first++);
        request.Raise();
        request.Listen(() => second++);

        await Assert.That(first).IsEqualTo(1);
        await Assert.That(second).IsEqualTo(0);
    }

    [Test]
    public async Task A_listener_that_left_is_not_raised_and_the_request_waits_for_the_next() {
        var request = new HeldRequest();
        var left = 0;
        var next = 0;
        Action leaving = () => left++;

        request.Listen(leaving);
        request.Ignore(leaving);
        request.Raise();
        request.Listen(() => next++);

        await Assert.That(left).IsEqualTo(0);
        await Assert.That(next).IsEqualTo(1);
    }
}
