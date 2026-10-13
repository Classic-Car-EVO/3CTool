using Google.Protobuf.WellKnownTypes;

using Grpc.Core;

using ThreeCTool.Feeder.Contracts;

using FeederClient = ThreeCTool.Feeder.Contracts.Feeder.FeederClient;

namespace ThreeCTool.Feeder.Tests;

public sealed class PairingTests
{
    [Fact]
    public async Task Pairing_OverTls_PinsCertificateAndAuthenticatesWithToken()
    {
        await using var feeder = await FeederHarness.StartAsync(home: false);
        var (client, pairing) = await feeder.PairAsync("Rob's PC");

        Assert.Equal(feeder.Fingerprint, pairing.Fingerprint);
        Assert.Equal(64, pairing.Fingerprint.Length);
        Assert.Equal(43, pairing.Token.Length);
        var info = await client.GetInfoAsync(new Empty());
        Assert.Equal("Test feeder", info.Name);
        Assert.Equal(pairing.FeederId, info.FeederId);

        var list = await feeder.Admin.ListPairingsAsync(new Empty());
        var paired = Assert.Single(list.Pairings);
        Assert.Equal("Rob's PC", paired.ClientName);
        Assert.Equal(pairing.PairingId, paired.PairingId);
        Assert.NotNull(paired.LastSeenAt);

        var status = await feeder.WaitStatusAsync(s => s.PairingCount == 1);
        Assert.False(status.PairingActive);
    }

    [Fact]
    public async Task Tls_WithoutValidToken_IsUnauthenticated()
    {
        await using var feeder = await FeederHarness.StartAsync(home: false);
        var bogus = new FeederClient(feeder.Track(FeederChannels.ForPairedFeeder(feeder.Address.ToString(), feeder.Fingerprint, "not-a-token")));

        var error = await Assert.ThrowsAsync<RpcException>(async () => await bogus.GetInfoAsync(new Empty()));
        Assert.Equal(StatusCode.Unauthenticated, error.StatusCode);
    }

    [Fact]
    public async Task Tls_WithWrongPinnedFingerprint_FailsToConnect()
    {
        await using var feeder = await FeederHarness.StartAsync(home: false);
        var (_, pairing) = await feeder.PairAsync();
        var wrongPin = new string('0', 64);
        var client = new FeederClient(feeder.Track(FeederChannels.ForPairedFeeder(feeder.Address.ToString(), wrongPin, pairing.Token)));

        var error = await Assert.ThrowsAsync<RpcException>(async () => await client.GetInfoAsync(new Empty()));
        Assert.Contains(error.StatusCode, new[] { StatusCode.Unavailable, StatusCode.Internal });
        Assert.Contains("SSL", error.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Revoke_InvalidatesToken()
    {
        await using var feeder = await FeederHarness.StartAsync(home: false);
        var (client, pairing) = await feeder.PairAsync();
        await client.GetInfoAsync(new Empty());

        await feeder.Admin.RevokePairingAsync(new PairingRef { PairingId = pairing.PairingId });

        var error = await Assert.ThrowsAsync<RpcException>(async () => await client.GetInfoAsync(new Empty()));
        Assert.Equal(StatusCode.Unauthenticated, error.StatusCode);
        Assert.Empty((await feeder.Admin.ListPairingsAsync(new Empty())).Pairings);
    }

    [Fact]
    public async Task Code_IsSingleUse()
    {
        await using var feeder = await FeederHarness.StartAsync(home: false);
        var code = await feeder.Admin.StartPairingAsync(new Empty());
        Assert.Matches("^[0-9]{6}$", code.Code);
        Assert.Equal(feeder.Fingerprint, code.CertificateFingerprint);
        Assert.True((await feeder.WaitStatusAsync(s => s.PairingActive)).PairingActive);

        await FeederPairing.PairAsync(feeder.Address.ToString(), code.Code, "First", (_, _) => Task.FromResult(true));
        var reuse = await Assert.ThrowsAsync<PairingRejectedException>(() =>
            FeederPairing.PairAsync(feeder.Address.ToString(), code.Code, "Second", (_, _) => Task.FromResult(true)));
        Assert.Contains("pairing", reuse.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WrongCodes_LockPairingAfterMaxAttempts()
    {
        await using var feeder = await FeederHarness.StartAsync(home: false);
        var code = await feeder.Admin.StartPairingAsync(new Empty());
        var wrong = code.Code == "000000" ? "111111" : "000000";
        for (var i = 0; i < 4; i++)
        {
            await Assert.ThrowsAsync<PairingRejectedException>(() =>
                FeederPairing.PairAsync(feeder.Address.ToString(), wrong, "Guess", (_, _) => Task.FromResult(true)));
        }

        await Assert.ThrowsAsync<PairingRejectedException>(() =>
            FeederPairing.PairAsync(feeder.Address.ToString(), wrong, "Guess", (_, _) => Task.FromResult(true)));

        // Even the right code is refused once the code is locked.
        await Assert.ThrowsAsync<PairingRejectedException>(() =>
            FeederPairing.PairAsync(feeder.Address.ToString(), code.Code, "Owner", (_, _) => Task.FromResult(true)));
        Assert.Empty((await feeder.Admin.ListPairingsAsync(new Empty())).Pairings);
    }

    [Fact]
    public async Task UnconfirmedFingerprint_DoesNotSendCode()
    {
        await using var feeder = await FeederHarness.StartAsync(home: false);
        var code = await feeder.Admin.StartPairingAsync(new Empty());

        await Assert.ThrowsAsync<PairingRejectedException>(() =>
            FeederPairing.PairAsync(feeder.Address.ToString(), code.Code, "PC", (_, _) => Task.FromResult(false)));

        // The code was never used, so it still pairs.
        var result = await FeederPairing.PairAsync(feeder.Address.ToString(), code.Code, "PC", (_, _) => Task.FromResult(true));
        Assert.Equal(feeder.Fingerprint, result.Fingerprint);
    }

    [Fact]
    public async Task LocalOnlyOperations_AreRefusedOverTls()
    {
        await using var feeder = await FeederHarness.StartAsync(home: false);
        var (client, pairing) = await feeder.PairAsync();
        var channel = feeder.Track(FeederChannels.ForPairedFeeder(feeder.Address.ToString(), pairing.Fingerprint, pairing.Token));
        var admin = new LocalAdmin.LocalAdminClient(channel);

        var adminError = await Assert.ThrowsAsync<RpcException>(async () => await admin.StartPairingAsync(new Empty()));
        Assert.Equal(StatusCode.PermissionDenied, adminError.StatusCode);
        var promptError = await Assert.ThrowsAsync<RpcException>(async () => await client.AnswerPromptAsync(new PromptAnswer { Id = "x", Confirmed = true }));
        Assert.Equal(StatusCode.PermissionDenied, promptError.StatusCode);
    }

    [Fact]
    public async Task Certificate_SurvivesRestart()
    {
        await using var feeder = await FeederHarness.StartAsync(home: false);
        var (_, pairing) = await feeder.PairAsync();

        await feeder.RestartFeederAsync();

        Assert.Equal(pairing.Fingerprint, feeder.Fingerprint);
        var client = new FeederClient(feeder.Track(FeederChannels.ForPairedFeeder(feeder.Address.ToString(), pairing.Fingerprint, pairing.Token)));
        Assert.Equal(pairing.FeederId, (await client.GetInfoAsync(new Empty())).FeederId);
    }
}
