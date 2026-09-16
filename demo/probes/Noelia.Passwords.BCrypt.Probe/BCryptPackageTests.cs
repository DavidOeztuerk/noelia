using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Noelia.Abstractions.Hosting;
using Noelia.Abstractions.Security.Passwords;
using Noelia.Passwords.BCrypt;

namespace Noelia.Passwords.BCrypt.Probe;

public sealed class BCryptPackageTests
{
    [Fact]
    public void BCrypt_module_hashes_and_verifies_without_retaining_the_password()
    {
        const string password = "BCRYPT-PROBE-CANARY-PASSWORD";
        var host = Host.CreateApplicationBuilder();
        var noelia = new NoeliaBuilder(
            host.Services, host.Configuration, host.Environment, "bcrypt-probe", [], []);

        noelia.UseBCryptPasswords(workFactor: 4);
        var composition = noelia.Build();
        using var provider = host.Services.BuildServiceProvider();
        var hasher = provider.GetRequiredKeyedService<IPasswordHasher>(PasswordHashing.PrimaryKey);
        var encoded = hasher.Hash(password);

        composition.Included.Should().Equal(BCryptNoeliaModule.Module);
        encoded.Should().StartWith("$2").And.NotContain(password);
        hasher.Verify(password, encoded).Should().Be(PasswordVerification.Success);
        hasher.Verify("wrong", encoded).Should().Be(PasswordVerification.Failed);
    }
}
