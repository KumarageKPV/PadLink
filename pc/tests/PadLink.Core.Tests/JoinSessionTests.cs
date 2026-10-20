using System.Net;
using Microsoft.Extensions.Time.Testing;
using PadLink.Core.Control;
using PadLink.Protocol.Control;
using PadLink.Protocol.Crypto;
using PadLink.Protocol.Tests;

namespace PadLink.Core.Tests;

// The join state machine without sockets (protocol/SPEC.md, "Join sequence" and "Join rules").
public sealed class JoinSessionTests : IDisposable
{
    private static readonly IPAddress PhoneAddress = IPAddress.Parse("192.168.1.23");

    // One of each message, and two frames that aren't one, for the table below.
    private static readonly Dictionary<string, byte[]> Samples = new()
    {
        ["hello"] = Bodies.Of(new PhoneSide().Hello()),
        ["pair_required"] = Bodies.Of(new PairRequired(Filled(JoinKeys.KeySize, 9), Filled(JoinKeys.NonceSize, 1))),
        ["pair"] = Bodies.Of(new Pair(Filled(JoinKeys.KeySize, 9), Filled(JoinKeys.HmacSize, 2))),
        ["challenge"] = Bodies.Of(new Challenge(Filled(JoinKeys.NonceSize, 3))),
        ["auth"] = Bodies.Of(new Auth(Filled(JoinKeys.HmacSize, 4))),
        ["welcome"] = Bodies.Of(new Welcome(0, "#FF0000", PadMode.Mixed, PadType.X360, 47821)),
        ["mode_changed"] = Bodies.Of(new ModeChanged(PadMode.AllDs4)),
        ["slot_changed"] = Bodies.Of(new SlotChanged(1, "#00FF00", PadType.Ds4)),
        ["ping"] = Bodies.Of(new Ping()),
        ["pong"] = Bodies.Of(new Pong()),
        ["battery"] = Bodies.Of(new Battery(57)),
        ["bye"] = Bodies.Of(new Bye()),
        ["error"] = Bodies.Of(new Error(ErrorCodes.BadMessage)),
        ["a type this version doesn't know"] = """{"type":"later","x":1}"""u8.ToArray(),
        ["not JSON"] = "{\"type\":"u8.ToArray(),
    };

    private readonly FakeTimeProvider _time = new();
    private readonly PairingCode _code;
    private readonly MemoryPairingStore _store = new();
    private readonly RecordingJoinHandler _handler = new();
    private readonly List<JoinSession> _sessions = [];

    public JoinSessionTests()
    {
        _code = new PairingCode(_time);
        _code.Show();
    }

    public enum Outcome
    {
        /// <summary>error bad_message, then close.</summary>
        BadMessage,

        /// <summary>pong, and the state stays.</summary>
        Pong,

        /// <summary>No reply, and the state stays.</summary>
        Ignored,

        /// <summary>No reply, then close.</summary>
        Closed,
    }

    // Every message in every state, except the one each state expects next, which the tests after this
    // one cover. Until welcome only that one, ping, pong, error and bye are in place; after it, the join
    // messages are out of place, and so is any message the PC sends.
    // JoinState is internal, so the theory takes its name.
    public static TheoryData<string, string, Outcome> EveryMessageInEveryState()
    {
        var data = new TheoryData<string, string, Outcome>();
        JoinState[] states = [JoinState.AwaitingHello, JoinState.AwaitingPair, JoinState.AwaitingAuth, JoinState.Joined];
        foreach (var state in states)
        {
            foreach (var name in Samples.Keys)
            {
                var expected = (state, name) switch
                {
                    (JoinState.AwaitingHello, "hello") or (JoinState.AwaitingPair, "pair") or (JoinState.AwaitingAuth, "auth") => (Outcome?)null,
                    (_, "ping") => Outcome.Pong,
                    (_, "pong" or "error" or "a type this version doesn't know") => Outcome.Ignored,
                    (JoinState.Joined, "battery") => Outcome.Ignored,
                    (_, "bye") => Outcome.Closed,
                    _ => Outcome.BadMessage,
                };
                if (expected is { } outcome)
                    data.Add(state.ToString(), name, outcome);
            }
        }
        return data;
    }

    public void Dispose()
    {
        foreach (var session in _sessions)
            session.Dispose();
        _code.Dispose();
    }

    [Theory]
    [MemberData(nameof(EveryMessageInEveryState))]
    public void A_message_in_a_state_gets_its_outcome(string stateName, string name, Outcome outcome)
    {
        var state = Enum.Parse<JoinState>(stateName);
        var session = SessionIn(state);

        var step = session.Receive(Samples[name]);

        switch (outcome)
        {
            case Outcome.BadMessage:
                Assert.Equal(new JoinStep(new Error(ErrorCodes.BadMessage), Close: true), step);
                Assert.Equal(JoinState.Closed, session.State);
                break;
            case Outcome.Pong:
                Assert.Equal(new JoinStep(new Pong(), Close: false), step);
                Assert.Equal(state, session.State);
                break;
            case Outcome.Ignored:
                Assert.Equal(new JoinStep(null, Close: false), step);
                Assert.Equal(state, session.State);
                break;
            case Outcome.Closed:
                Assert.Equal(new JoinStep(null, Close: true), step);
                Assert.Equal(JoinState.Closed, session.State);
                break;
        }
    }

    [Fact]
    public void A_new_phone_pairs_with_the_shown_code_and_joins()
    {
        var phone = new PhoneSide();
        var session = NewSession();

        var request = Assert.IsType<PairRequired>(Reply(session.Receive(Bodies.Of(phone.Hello()))));
        Assert.Equal(JoinState.AwaitingPair, session.State);
        var shown = _code.Current!;
        var welcome = Reply(session.Receive(Bodies.Of(phone.Pair(request, shown))));

        Assert.Equal(new Welcome(0, "#FF8000", PadMode.AllDs4, PadType.Ds4, RecordingJoinHandler.UdpPort), welcome);
        Assert.Equal(JoinState.Joined, session.State);
        Assert.Equal(phone.PairingKey, _store.KeyOf(phone.DeviceId));
        var (joined, sessionKey) = Assert.Single(_handler.Joins);
        Assert.Same(session.Phone, joined);
        Assert.Equal(phone.SessionKey, sessionKey);
        Assert.Equal(
            (phone.DeviceId, PhoneSide.Name, PhonePlatform.Android, "1.0.0", PhoneAddress),
            (joined.DeviceId, joined.DeviceName, joined.Platform, joined.AppVersion, joined.Address));
        Assert.NotEqual(shown, _code.Current); // used up
    }

    [Fact]
    public void A_known_phone_answers_the_challenge_and_joins()
    {
        var phone = PhoneSide.Paired(_store);
        var session = NewSession();

        var challenge = Assert.IsType<Challenge>(Reply(session.Receive(Bodies.Of(phone.Hello()))));
        Assert.Equal(JoinState.AwaitingAuth, session.State);
        var welcome = Assert.IsType<Welcome>(Reply(session.Receive(Bodies.Of(phone.Auth(challenge)))));

        Assert.Equal(0, welcome.Slot);
        Assert.Equal(JoinState.Joined, session.State);
        Assert.Equal(phone.SessionKey, Assert.Single(_handler.Joins).SessionKey);
    }

    [Fact]
    public void A_known_phone_is_challenged_even_while_the_code_is_hidden()
    {
        var phone = PhoneSide.Paired(_store);
        _code.Hide();
        var session = NewSession();

        var challenge = Assert.IsType<Challenge>(Reply(session.Receive(Bodies.Of(phone.Hello()))));

        Assert.IsType<Welcome>(Reply(session.Receive(Bodies.Of(phone.Auth(challenge)))));
    }

    [Fact]
    public void Each_join_gets_a_fresh_pc_key_and_nonce()
    {
        var first = PairRequest(NewSession(), new PhoneSide());
        var second = PairRequest(NewSession(), new PhoneSide());
        var challenge = Assert.IsType<Challenge>(Reply(NewSession().Receive(Bodies.Of(PhoneSide.Paired(_store, "known").Hello()))));

        Assert.NotEqual(first.Key, second.Key);
        Assert.NotEqual(first.Nonce, second.Nonce);
        Assert.NotEqual(first.Nonce, challenge.Nonce);
    }

    [Fact]
    public void A_pair_replayed_from_another_connection_fails_even_with_the_right_code()
    {
        var phone = new PhoneSide();
        var recorded = phone.Pair(PairRequest(NewSession(), phone), _code.Current!);
        var session = NewSession();
        PairRequest(session, phone);

        var step = session.Receive(Bodies.Of(recorded));

        Assert.Equal(new JoinStep(new Error(ErrorCodes.BadProof), Close: true), step);
        Assert.Empty(_handler.Calls);
    }

    [Fact]
    public void A_wrong_code_gets_bad_proof_and_stores_nothing()
    {
        var phone = new PhoneSide();
        var session = NewSession();
        var request = PairRequest(session, phone);

        var step = session.Receive(Bodies.Of(phone.Pair(request, Other(_code.Current!))));

        Assert.Equal(new JoinStep(new Error(ErrorCodes.BadProof), Close: true), step);
        Assert.Equal(JoinState.Closed, session.State);
        Assert.Null(_store.KeyOf(phone.DeviceId));
        Assert.Empty(_handler.Calls);
    }

    [Fact]
    public void Five_wrong_codes_across_connections_stop_pairing_for_30_seconds_then_a_new_code_works()
    {
        var shown = _code.Current!;

        for (var i = 1; i <= PairingCode.MaxWrongTries; i++)
        {
            Assert.Equal(shown, _code.Current);
            var phone = new PhoneSide();
            var session = NewSession();
            var step = session.Receive(Bodies.Of(phone.Pair(PairRequest(session, phone), Other(shown))));
            Assert.Equal(new Error(ErrorCodes.BadProof), step.Reply);
        }

        Assert.Null(_code.Current);
        var late = new PhoneSide();
        var lateSession = NewSession();
        Assert.Equal(
            new Error(ErrorCodes.BadProof),
            lateSession.Receive(Bodies.Of(late.Pair(PairRequest(lateSession, late), shown))).Reply);
        _time.Advance(PairingCode.Cooldown);
        var next = _code.Current!;
        Assert.NotEqual(shown, next);
        var phoneAfter = new PhoneSide();
        var sessionAfter = NewSession();
        Assert.IsType<Welcome>(Reply(sessionAfter.Receive(Bodies.Of(phoneAfter.Pair(PairRequest(sessionAfter, phoneAfter), next)))));
    }

    [Fact]
    public void A_code_that_expires_between_pair_required_and_pair_fails()
    {
        var phone = new PhoneSide();
        var session = NewSession();
        var request = PairRequest(session, phone);
        var shown = _code.Current!;

        _time.Advance(PairingCode.Lifetime);

        Assert.Equal(new Error(ErrorCodes.BadProof), session.Receive(Bodies.Of(phone.Pair(request, shown))).Reply);
    }

    [Fact]
    public void While_the_code_is_hidden_pairing_fails()
    {
        var phone = new PhoneSide();
        var shown = _code.Current!;
        var session = NewSession();
        var request = PairRequest(session, phone);
        _code.Hide();

        Assert.Equal(new Error(ErrorCodes.BadProof), session.Receive(Bodies.Of(phone.Pair(request, shown))).Reply);
    }

    public static TheoryData<string> RejectedPublicKeys => Vectors.Names("crypto.json", "rejectedPublicKeys");

    [Theory]
    [MemberData(nameof(RejectedPublicKeys))]
    public void A_pair_key_with_an_all_zero_shared_secret_gets_bad_message_and_costs_no_try(string name)
    {
        var key = Vectors.Hex(Vectors.Get("crypto.json", "rejectedPublicKeys", name)["publicKey"]);
        var phone = new PhoneSide();
        var session = NewSession();
        var request = PairRequest(session, phone);
        var shown = _code.Current!;

        var step = session.Receive(Bodies.Of(new Pair(key, JoinKeys.Proof(shown, request.Key, key))));

        Assert.Equal(new JoinStep(new Error(ErrorCodes.BadMessage), Close: true), step);
        Assert.Equal(shown, _code.Current); // neither used up nor a wrong try
        for (var i = 0; i < PairingCode.MaxWrongTries - 1; i++)
            TryWrongCode();
        Assert.Equal(shown, _code.Current);
    }

    [Fact]
    public void A_wrong_auth_mac_gets_bad_auth()
    {
        var phone = PhoneSide.Paired(_store);
        var session = NewSession();
        var challenge = Assert.IsType<Challenge>(Reply(session.Receive(Bodies.Of(phone.Hello()))));
        var auth = phone.Auth(challenge);

        var step = session.Receive(Bodies.Of(new Auth(Vectors.Flipped(auth.Mac, 255))));

        Assert.Equal(new JoinStep(new Error(ErrorCodes.BadAuth), Close: true), step);
        Assert.Empty(_handler.Calls);
    }

    [Fact]
    public void An_auth_made_for_another_challenge_gets_bad_auth()
    {
        var phone = PhoneSide.Paired(_store);
        var first = NewSession();
        var recorded = phone.Auth(Assert.IsType<Challenge>(Reply(first.Receive(Bodies.Of(phone.Hello())))));
        var again = phone.Reconnect();
        var session = NewSession();
        session.Receive(Bodies.Of(again.Hello()));

        Assert.Equal(new Error(ErrorCodes.BadAuth), session.Receive(Bodies.Of(recorded)).Reply);
    }

    [Fact]
    public void Another_proto_gets_unsupported_proto()
    {
        var session = NewSession();

        var step = session.Receive(Bodies.Of(new PhoneSide().Hello(proto: 2)));

        Assert.Equal(new JoinStep(new Error(ErrorCodes.UnsupportedProto), Close: true), step);
        Assert.Equal(JoinState.Closed, session.State);
    }

    [Fact]
    public void With_every_slot_taken_a_phone_gets_full()
    {
        var handler = new RecordingJoinHandler(slotCount: 0);
        var phone = PhoneSide.Paired(_store);
        var session = Track(new JoinSession(PhoneAddress, _code, _store, handler.Join));
        var challenge = Assert.IsType<Challenge>(Reply(session.Receive(Bodies.Of(phone.Hello()))));

        var step = session.Receive(Bodies.Of(phone.Auth(challenge)));

        Assert.Equal(new JoinStep(new Error(ErrorCodes.Full), Close: true), step);
        Assert.Equal(JoinState.Closed, session.State);
        Assert.Null(session.Phone);
        Assert.Equal([$"full {phone.DeviceId}"], handler.Calls);
    }

    [Fact]
    public void A_new_phone_that_finds_every_slot_taken_stays_paired()
    {
        var handler = new RecordingJoinHandler(slotCount: 0);
        var phone = new PhoneSide();
        var session = Track(new JoinSession(PhoneAddress, _code, _store, handler.Join));
        var request = Assert.IsType<PairRequired>(Reply(session.Receive(Bodies.Of(phone.Hello()))));

        Assert.Equal(new Error(ErrorCodes.Full), session.Receive(Bodies.Of(phone.Pair(request, _code.Current!))).Reply);

        Assert.Equal(phone.PairingKey, _store.KeyOf(phone.DeviceId));
    }

    [Fact]
    public void A_joined_phone_reports_its_battery()
    {
        var session = SessionIn(JoinState.Joined);

        session.Receive(Bodies.Of(new Battery(57)));

        Assert.Equal(57, session.Phone!.Battery);
    }

    [Fact]
    public void A_closed_session_takes_no_more_frames()
    {
        var session = NewSession();
        session.Receive(Bodies.Of(new Bye()));

        Assert.Throws<InvalidOperationException>(() => session.Receive(Bodies.Of(new Ping())));
    }

    private static ControlMessage Reply(JoinStep step)
    {
        Assert.False(step.Close, $"closed with {step.Reply}");
        return step.Reply!;
    }

    private static string Other(string code) => code == "000000" ? "000001" : "000000";

    private static byte[] Filled(int size, byte value) => Enumerable.Repeat(value, size).ToArray();

    private JoinSession NewSession() => Track(new JoinSession(PhoneAddress, _code, _store, _handler.Join));

    private JoinSession Track(JoinSession session)
    {
        _sessions.Add(session);
        return session;
    }

    private static PairRequired PairRequest(JoinSession session, PhoneSide phone) =>
        Assert.IsType<PairRequired>(Reply(session.Receive(Bodies.Of(phone.Hello()))));

    private void TryWrongCode()
    {
        var phone = new PhoneSide();
        var session = NewSession();
        session.Receive(Bodies.Of(phone.Pair(PairRequest(session, phone), Other(_code.Current!))));
    }

    private JoinSession SessionIn(JoinState state)
    {
        var session = NewSession();
        switch (state)
        {
            case JoinState.AwaitingPair:
                PairRequest(session, new PhoneSide("new phone"));
                break;
            case JoinState.AwaitingAuth:
                session.Receive(Bodies.Of(PhoneSide.Paired(_store, "known phone").Hello()));
                break;
            case JoinState.Joined:
                var phone = PhoneSide.Paired(_store, "joined phone");
                var challenge = Assert.IsType<Challenge>(Reply(session.Receive(Bodies.Of(phone.Hello()))));
                Assert.IsType<Welcome>(Reply(session.Receive(Bodies.Of(phone.Auth(challenge)))));
                break;
        }
        Assert.Equal(state, session.State);
        return session;
    }
}
