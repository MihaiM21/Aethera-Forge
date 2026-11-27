namespace Aethera.Domain.Tests;

public sealed class ImageTagRequestTests
{
    private const string Hex = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Theory]
    [InlineData("3f2a9c1", "ghcr.io/acme/app", "3f2a9c1")]
    [InlineData("v1.4.0", "ghcr.io/acme/app", "v1.4.0")]
    [InlineData("  main_2026-11  ", "ghcr.io/acme/app", "main_2026-11")]
    [InlineData("sha256:" + Hex, "ghcr.io/acme/app", "@sha256:" + Hex)]
    [InlineData("@sha256:" + Hex, "ghcr.io/acme/app", "@sha256:" + Hex)]
    [InlineData("ghcr.io/acme/app:3f2a9c1", "ghcr.io/acme/app", "3f2a9c1")]
    [InlineData("GHCR.IO/acme/app:3f2a9c1", "ghcr.io/acme/app", "3f2a9c1")]
    [InlineData("ghcr.io/acme/app@sha256:" + Hex, "ghcr.io/acme/app", "@sha256:" + Hex)]
    [InlineData("nginx:1.28", "docker.io/library/nginx", "1.28")]
    [InlineData("docker.io/library/nginx:1.28", "nginx", "1.28")]
    [InlineData("library/nginx:1.28", "index.docker.io/nginx", "1.28")]
    [InlineData("localhost:5000/app:dev", "localhost:5000/app", "dev")]
    public void Accepts_tags_digests_and_references_of_the_same_repository(string requested, string repository, string expected)
    {
        Assert.Equal(ImageTagProblem.None, ImageTagRequest.TryNormalize(requested, repository, out var tag));
        Assert.Equal(expected, tag);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("-dash")]
    [InlineData(".dot")]
    [InlineData("with space")]
    [InlineData("sha256:abc")]
    [InlineData("sha256:" + Hex + "00")]
    [InlineData("ghcr.io/acme/app:")]
    [InlineData("ghcr.io/acme/app")]
    [InlineData("localhost:5000/app")]
    [InlineData("ghcr.io/acme/app@sha256:nothex")]
    [InlineData("ghcr.io//app:1")]
    [InlineData("ghcr.io/acme/app:1;rm -rf /")]
    public void Refuses_malformed_values(string? requested) =>
        Assert.Equal(ImageTagProblem.Invalid, ImageTagRequest.TryNormalize(requested, "ghcr.io/acme/app", out _));

    [Fact]
    public void Refuses_tags_longer_than_Dockers_limit()
    {
        Assert.Equal(ImageTagProblem.None, ImageTagRequest.TryNormalize(new string('a', 128), "app", out _));
        Assert.Equal(ImageTagProblem.Invalid, ImageTagRequest.TryNormalize(new string('a', 129), "app", out _));
    }

    [Theory]
    [InlineData("ghcr.io/other/app:1")]
    [InlineData("docker.io/acme/app:1")]
    [InlineData("redis:7")]
    [InlineData("ghcr.io/acme/app-2:1")]
    public void Refuses_another_repository(string requested) =>
        Assert.Equal(ImageTagProblem.RepositoryMismatch, ImageTagRequest.TryNormalize(requested, "ghcr.io/acme/app", out _));
}
