using Aethera.Domain;

namespace Aethera.Domain.Tests;

public sealed class DomainAssemblyTests
{
    [Fact]
    public void AssemblyName_IsStable() => Assert.Equal("Aethera.Domain", DomainAssembly.Name);
}
