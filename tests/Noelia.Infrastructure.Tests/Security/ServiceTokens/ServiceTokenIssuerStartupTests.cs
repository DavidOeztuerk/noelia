using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Noelia.Infrastructure.Extensions;
using Microsoft.Extensions.Options;
using Noelia.Abstractions.Hosting;
using ConfigurationException = Noelia.Core.Exceptions.ConfigurationException;
using Noelia.Infrastructure.Builder;
using Noelia.Infrastructure.Security.Keys;
using Noelia.Infrastructure.Security.ServiceTokens;
using static Noelia.Infrastructure.Tests.Security.ServiceTokens.ServiceTokenFixture;

namespace Noelia.Infrastructure.Tests.Security.ServiceTokens;

/// <summary>
/// What the issuer refuses to start with, and that each builder call has an
/// effect on what it does.
/// </summary>
[Trait("Category", "Unit")]
public class ServiceTokenIssuerStartupTests
{
    // ---- the effect of each call -------------------------------------------------

    [Fact]
    public void UseServiceTokenIssuer_registers_the_issuer_and_the_module()
    {
        var config = StandardConfig();
        using var provider = Compose(config, n => Standard(n, config)).BuildServiceProvider();

        provider.GetRequiredService<IServiceTokenIssuer>().Should().NotBeNull();
        provider.GetRequiredService<NoeliaComposition>().Included
            .Should().Contain(NoeliaModule.ServiceTokenIssuing);
    }

    [Fact]
    public async Task Clients_makes_exactly_the_named_clients_exist()
    {
        // The hash of "stranger" is in the configuration, the id is not in the code.
        var config = StandardConfig(("NOELIA_M2M_CLIENT_STRANGER_SECRET_HASH", ServiceClientSecret.HashOf(
            ServiceClientSecret.Generate("stranger").Secret)));
        using var provider = Compose(config, n => Standard(n, config)).BuildServiceProvider();
        var issuer = provider.GetRequiredService<IServiceTokenIssuer>();

        (await issuer.IssueAsync("notification", KnownVector, "identity")).Should().NotBeNull();
        (await issuer.IssueAsync("advisor", OtherVector, "identity")).Should().NotBeNull();
        (await issuer.IssueAsync("stranger", KnownVector, "identity")).Should().BeNull();
    }

    [Fact]
    public async Task Audiences_decides_which_audiences_are_issued_for()
    {
        var config = StandardConfig();
        using var provider = Compose(config, n => Standard(n, config)).BuildServiceProvider();
        var issuer = provider.GetRequiredService<IServiceTokenIssuer>();

        (await issuer.IssueAsync("notification", KnownVector, "billing")).Should().NotBeNull();
        (await issuer.IssueAsync("notification", KnownVector, "elsewhere")).Should().BeNull();
    }

    [Fact]
    public async Task Lifetime_sets_how_long_the_token_lives()
    {
        var config = StandardConfig();
        using var provider = Compose(config, n => n
            .UseJwt(jwt => jwt.Issue(KeyPair.PrivateKey, KeyPair.Kid))
            .UseServiceTokenIssuer(i => i
                .Clients(config, "notification")
                .Audiences("identity")
                .Lifetime(TimeSpan.FromMinutes(9)))).BuildServiceProvider();

        var token = await provider.GetRequiredService<IServiceTokenIssuer>()
            .IssueAsync("notification", KnownVector, "identity");

        token!.ExpiresIn.Should().Be(TimeSpan.FromMinutes(9));
    }

    [Fact]
    public async Task Without_Lifetime_the_default_is_five_minutes()
    {
        var config = StandardConfig();
        using var provider = Compose(config, n => Standard(n, config)).BuildServiceProvider();

        var token = await provider.GetRequiredService<IServiceTokenIssuer>()
            .IssueAsync("notification", KnownVector, "identity");

        token!.ExpiresIn.Should().Be(ServiceTokenDefaults.DefaultLifetime);
    }

    // ---- formats and registers: refused where they are written ---------------------

    [Fact]
    public void A_missing_current_hash_names_the_variable_and_not_a_value()
    {
        var config = Config(("NOELIA_M2M_CLIENT_NOTIFICATION_SECRET_HASH", KnownHash));

        var compose = () => Compose(config, n => n
            .UseJwt(jwt => jwt.Issue(KeyPair.PrivateKey, KeyPair.Kid))
            .UseServiceTokenIssuer(i => i.Clients(config, "notification", "advisor").Audiences("identity")));

        compose.Should().Throw<ConfigurationException>()
            .WithMessage("*NOELIA_M2M_CLIENT_ADVISOR_SECRET_HASH*")
            .Which.Message.Should().NotContain(KnownHash);
    }

    [Theory]
    [InlineData("md5:rWpn3cMgvuFTUlyssEse2ZRB29ydKiG75UV2pFxISQU")]        // other prefix
    [InlineData("sha256:rWpn3cMgvuFTUlyssEse2ZRB29ydKiG75UV2pFxIS")]       // too short
    [InlineData("sha256:rWpn3cMgvuFTUlyssEse2ZRB29ydKiG75UV2pFxISQUA")]    // too long
    [InlineData("rWpn3cMgvuFTUlyssEse2ZRB29ydKiG75UV2pFxISQU")]            // no prefix
    [InlineData("vTFQKHO5s-d6tcHO3FIL9VsySJbH4BtwgcszEdEnybw")]            // the plain secret
    [InlineData("sha256:rWpn3cMgvuFTUlyssEse2ZRB29ydKiG75UV2pFxISQ=")]     // padding
    [InlineData("sha256:rWpn3cMgvuFTUlyssEse2ZRB29ydKiG75UV2pFxISQV")]     // non-canonical last character
    public void A_hash_in_another_shape_is_refused_and_the_message_does_not_repeat_it(string hash)
    {
        var config = Config(("NOELIA_M2M_CLIENT_NOTIFICATION_SECRET_HASH", hash));

        var compose = () => Compose(config, n => n
            .UseJwt(jwt => jwt.Issue(KeyPair.PrivateKey, KeyPair.Kid))
            .UseServiceTokenIssuer(i => i.Clients(config, "notification").Audiences("identity")));

        compose.Should().Throw<ConfigurationException>()
            .WithMessage("*NOELIA_M2M_CLIENT_NOTIFICATION_SECRET_HASH*")
            .Which.Message.Should().NotContain(hash);
    }

    [Fact]
    public void A_malformed_previous_hash_is_refused_too()
    {
        var config = Config(
            ("NOELIA_M2M_CLIENT_NOTIFICATION_SECRET_HASH", KnownHash),
            ("NOELIA_M2M_CLIENT_NOTIFICATION_SECRET_HASH_PREVIOUS", "sha256:short"));

        var compose = () => Compose(config, n => n
            .UseJwt(jwt => jwt.Issue(KeyPair.PrivateKey, KeyPair.Kid))
            .UseServiceTokenIssuer(i => i.Clients(config, "notification").Audiences("identity")));

        compose.Should().Throw<ConfigurationException>()
            .WithMessage("*NOELIA_M2M_CLIENT_NOTIFICATION_SECRET_HASH_PREVIOUS*");
    }

    [Fact]
    public void Two_clients_with_one_current_hash_do_not_start()
    {
        var config = Config(
            ("NOELIA_M2M_CLIENT_A_SECRET_HASH", KnownHash),
            ("NOELIA_M2M_CLIENT_B_SECRET_HASH", KnownHash));

        var compose = () => Compose(config, n => n
            .UseJwt(jwt => jwt.Issue(KeyPair.PrivateKey, KeyPair.Kid))
            .UseServiceTokenIssuer(i => i.Clients(config, "a", "b").Audiences("identity")));

        compose.Should().Throw<InvalidOperationException>().WithMessage("*share*");
    }

    /// <summary>The old shared value left in a previous slot is the case that matters.</summary>
    [Fact]
    public void One_previous_hash_shared_by_two_clients_does_not_start()
    {
        var config = Config(
            ("NOELIA_M2M_CLIENT_A_SECRET_HASH", KnownHash),
            ("NOELIA_M2M_CLIENT_B_SECRET_HASH", OtherHash),
            ("NOELIA_M2M_CLIENT_A_SECRET_HASH_PREVIOUS", ServiceClientSecret.HashOf(ServiceClientSecret.Generate("x").Secret)),
            ("NOELIA_M2M_CLIENT_B_SECRET_HASH_PREVIOUS", ServiceClientSecret.HashOf(ServiceClientSecret.Generate("x").Secret)));
        // Two different previous hashes are fine; only a shared one is not.
        var starts = () => Compose(config, n => n
            .UseJwt(jwt => jwt.Issue(KeyPair.PrivateKey, KeyPair.Kid))
            .UseServiceTokenIssuer(i => i.Clients(config, "a", "b").Audiences("identity")));
        starts.Should().NotThrow();

        var shared = ServiceClientSecret.HashOf(ServiceClientSecret.Generate("x").Secret);
        var clash = Config(
            ("NOELIA_M2M_CLIENT_A_SECRET_HASH", KnownHash),
            ("NOELIA_M2M_CLIENT_B_SECRET_HASH", OtherHash),
            ("NOELIA_M2M_CLIENT_A_SECRET_HASH_PREVIOUS", shared),
            ("NOELIA_M2M_CLIENT_B_SECRET_HASH_PREVIOUS", shared));

        var compose = () => Compose(clash, n => n
            .UseJwt(jwt => jwt.Issue(KeyPair.PrivateKey, KeyPair.Kid))
            .UseServiceTokenIssuer(i => i.Clients(clash, "a", "b").Audiences("identity")));

        compose.Should().Throw<InvalidOperationException>().WithMessage("*share*");
    }

    [Fact]
    public void A_previous_hash_equal_to_another_clients_current_hash_does_not_start()
    {
        var config = Config(
            ("NOELIA_M2M_CLIENT_A_SECRET_HASH", KnownHash),
            ("NOELIA_M2M_CLIENT_B_SECRET_HASH", OtherHash),
            ("NOELIA_M2M_CLIENT_B_SECRET_HASH_PREVIOUS", KnownHash));

        var compose = () => Compose(config, n => n
            .UseJwt(jwt => jwt.Issue(KeyPair.PrivateKey, KeyPair.Kid))
            .UseServiceTokenIssuer(i => i.Clients(config, "a", "b").Audiences("identity")));

        compose.Should().Throw<InvalidOperationException>().WithMessage("*share*");
    }

    [Theory]
    [InlineData("Notification")]
    [InlineData("1service")]
    [InlineData("has_underscore")]
    [InlineData("")]
    [InlineData("a-very-long-client-id-that-goes-past-the-limit-of-48-characters")]
    public void A_client_id_outside_the_pattern_is_refused(string clientId)
    {
        var config = StandardConfig();

        var compose = () => Compose(config, n => n
            .UseJwt(jwt => jwt.Issue(KeyPair.PrivateKey, KeyPair.Kid))
            .UseServiceTokenIssuer(i => i.Clients(config, clientId).Audiences("identity")));

        compose.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Naming_one_client_twice_is_refused()
    {
        var config = StandardConfig();

        var compose = () => Compose(config, n => n
            .UseJwt(jwt => jwt.Issue(KeyPair.PrivateKey, KeyPair.Kid))
            .UseServiceTokenIssuer(i => i
                .Clients(config, "notification")
                .Clients(config, "notification")
                .Audiences("identity")));

        compose.Should().Throw<ArgumentException>().WithMessage("*notification*");
    }

    [Fact]
    public void An_issuer_without_clients_is_refused()
    {
        var config = StandardConfig();

        var compose = () => Compose(config, n => n
            .UseJwt(jwt => jwt.Issue(KeyPair.PrivateKey, KeyPair.Kid))
            .UseServiceTokenIssuer(i => i.Audiences("identity")));

        compose.Should().Throw<InvalidOperationException>().WithMessage("*Clients*");
    }

    [Fact]
    public void An_empty_audience_set_is_refused()
    {
        var config = StandardConfig();

        var compose = () => Compose(config, n => n
            .UseJwt(jwt => jwt.Issue(KeyPair.PrivateKey, KeyPair.Kid))
            .UseServiceTokenIssuer(i => i.Clients(config, "notification")));

        compose.Should().Throw<InvalidOperationException>().WithMessage("*Audiences*");
    }

    [Fact]
    public void An_audience_outside_the_pattern_is_refused()
    {
        var config = StandardConfig();

        var compose = () => Compose(config, n => n
            .UseJwt(jwt => jwt.Issue(KeyPair.PrivateKey, KeyPair.Kid))
            .UseServiceTokenIssuer(i => i.Clients(config, "notification").Audiences("Not Valid")));

        compose.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(30)]
    [InlineData(901)]
    [InlineData(-60)]
    public void A_lifetime_outside_one_to_fifteen_minutes_is_refused(int seconds)
    {
        var config = StandardConfig();

        var compose = () => Compose(config, n => n
            .UseJwt(jwt => jwt.Issue(KeyPair.PrivateKey, KeyPair.Kid))
            .UseServiceTokenIssuer(i => i
                .Clients(config, "notification")
                .Audiences("identity")
                .Lifetime(TimeSpan.FromSeconds(seconds))));

        compose.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData(60)]
    [InlineData(900)]
    public void The_limits_themselves_are_allowed(int seconds)
    {
        var config = StandardConfig();

        var compose = () => Compose(config, n => n
            .UseJwt(jwt => jwt.Issue(KeyPair.PrivateKey, KeyPair.Kid))
            .UseServiceTokenIssuer(i => i
                .Clients(config, "notification")
                .Audiences("identity")
                .Lifetime(TimeSpan.FromSeconds(seconds))));

        compose.Should().NotThrow();
    }

    // ---- what needs the key ring: refused at start, in either call order ---------

    [Fact]
    public void A_shared_secret_ring_does_not_start()
    {
        var config = StandardConfig(
            ("JwtSettings:Secret", "a-shared-secret-that-is-long-enough-for-hmac-sha256-0123456789"));
        using var provider = Compose(config, n => n
            .UseJwt(jwt => jwt.FromSharedSecret())
            .UseServiceTokenIssuer(i => i.Clients(config, "notification").Audiences("identity")))
            .BuildServiceProvider();

        var resolve = () => provider.GetRequiredService<IServiceTokenIssuer>();

        resolve.Should().Throw<OptionsValidationException>().WithMessage("*asymmetric*");
    }

    [Fact]
    public void A_verify_only_ring_does_not_start()
    {
        var config = StandardConfig();
        using var provider = Compose(config, n => n
            .UseJwt(jwt => jwt.VerifyOnly(KeyPair.PublicKey, KeyPair.Kid))
            .UseServiceTokenIssuer(i => i.Clients(config, "notification").Audiences("identity")))
            .BuildServiceProvider();

        var resolve = () => provider.GetRequiredService<IServiceTokenIssuer>();

        resolve.Should().Throw<OptionsValidationException>().WithMessage("*signing key*");
    }

    [Fact]
    public void An_audience_equal_to_the_person_audience_does_not_start()
    {
        var config = StandardConfig();
        using var provider = Compose(config, n => n
            .UseJwt(jwt => jwt.Issue(KeyPair.PrivateKey, KeyPair.Kid))
            .UseServiceTokenIssuer(i => i.Clients(config, "notification").Audiences("identity", PersonAudience)))
            .BuildServiceProvider();

        var resolve = () => provider.GetRequiredService<IServiceTokenIssuer>();

        resolve.Should().Throw<OptionsValidationException>().WithMessage("*persons*");
    }

    [Fact]
    public void The_order_of_UseJwt_and_UseServiceTokenIssuer_does_not_matter()
    {
        var config = StandardConfig();

        // Issuer first, a ring that cannot sign second: still refused.
        using var refused = Compose(config, n => n
            .UseServiceTokenIssuer(i => i.Clients(config, "notification").Audiences("identity"))
            .UseJwt(jwt => jwt.VerifyOnly(KeyPair.PublicKey, KeyPair.Kid)))
            .BuildServiceProvider();
        var resolve = () => refused.GetRequiredService<IServiceTokenIssuer>();
        resolve.Should().Throw<OptionsValidationException>();

        // And the working combination works in the other order too.
        using var accepted = Compose(config, n => n
            .UseServiceTokenIssuer(i => i.Clients(config, "notification").Audiences("identity"))
            .UseJwt(jwt => jwt.Issue(KeyPair.PrivateKey, KeyPair.Kid)))
            .BuildServiceProvider();
        accepted.GetRequiredService<IServiceTokenIssuer>().Should().NotBeNull();
    }

    /// <summary>
    /// The refusal comes when the host starts, not on the first request: nobody
    /// asks for a token until the service that needs one is already failing.
    /// </summary>
    [Fact]
    public async Task The_checks_run_when_the_host_starts()
    {
        var config = StandardConfig();
        using var host = new HostBuilder()
            .ConfigureServices((context, services) => services
                .AddLogging()
                .AddNoelia(config, context.HostingEnvironment, "identity", n => n
                    .UseJwt(jwt => jwt.VerifyOnly(KeyPair.PublicKey, KeyPair.Kid))
                    .UseServiceTokenIssuer(i => i.Clients(config, "notification").Audiences("identity"))))
            .Build();

        var start = async () => await host.StartAsync();

        await start.Should().ThrowAsync<OptionsValidationException>();
    }
}
