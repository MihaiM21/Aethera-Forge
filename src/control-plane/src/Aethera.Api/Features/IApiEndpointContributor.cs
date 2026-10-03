namespace Aethera.Api.Features;

/// <summary>
/// Extension point: any registered contributor maps extra endpoints into the <c>/api/v1</c> group (inheriting the secure-by-default
/// authorization and the conventions). <c>Program.cs</c> calls every contributor after the feature modules. Used by the test factory to add
/// throw-away endpoints; feature code should use its own <c>MapXxx</c> instead.
/// </summary>
public interface IApiEndpointContributor
{
    void MapEndpoints(IEndpointRouteBuilder api);
}
