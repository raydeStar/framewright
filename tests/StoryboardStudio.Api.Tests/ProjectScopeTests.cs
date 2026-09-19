using StoryboardStudio.Api.Services;

namespace StoryboardStudio.Api.Tests;

public sealed class ProjectScopeTests
{
    [Fact]
    public void RequestScopeKeepsItsOriginalProjectWhenTheGlobalSelectionChanges()
    {
        var projectA = Guid.NewGuid();
        var projectB = Guid.NewGuid();
        var explicitlyBound = Guid.NewGuid();
        var registry = new ActiveProjectRegistry();
        registry.Set(projectA);

        var request = new ProjectScope(registry);
        registry.Set(projectB);

        Assert.Equal(projectA, request.ProjectId);
        request.Bind(explicitlyBound);
        Assert.Equal(explicitlyBound, request.ProjectId);
    }
}
