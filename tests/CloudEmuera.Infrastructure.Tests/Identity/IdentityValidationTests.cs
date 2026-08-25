using CloudEmuera.Infrastructure.Identity;

namespace CloudEmuera.Infrastructure.Tests.Identity;

public sealed class IdentityValidationTests
{
    [Fact]
    [Trait("Category", "IdentityPassword")]
    public void BootstrapPasswordMayBeShortAndUseNoLettersOrDigits()
    {
        IdentityValidation.ValidateBootstrapPassword("!");
    }

    [Theory]
    [InlineData("!!!!!!!!")]
    [InlineData("12345678")]
    [InlineData("abcdefgh")]
    [InlineData("abc12345")]
    [InlineData("密码123456")]
    [Trait("Category", "IdentityPassword")]
    public void ChangedPasswordAcceptsEightCharactersWithoutCompositionRules(string password)
    {
        IdentityValidation.ValidatePassword(password);
    }

    [Theory]
    [InlineData("a123456")]
    [InlineData("1234567")]
    [InlineData("abc123\0")]
    [Trait("Category", "IdentityPassword")]
    public void ChangedPasswordRejectsShortOrMalformedPasswords(string password)
    {
        Assert.Throws<IdentityValidationException>(() => IdentityValidation.ValidatePassword(password));
    }
}
