using Bunit;
using Daleel.Web.Components.Shared;
using Daleel.Web.Services;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using Xunit;

namespace Daleel.Web.Tests;

/// <summary>Renders SafeImage to prove external images are blurred until the user reveals them.</summary>
public class SafeImageTests : TestContext
{
    public SafeImageTests()
    {
        Services.AddMudServices();
        Services.AddScoped<BrowserStore>();
        JSInterop.Mode = JSRuntimeMode.Loose; // localStorage reads return null ⇒ blur defaults ON
    }

    [Fact]
    public void Renders_Blurred_ByDefault()
    {
        var cut = RenderComponent<SafeImage>(p => p
            .Add(x => x.Src, "https://images.example.com/photo.jpg")
            .Add(x => x.Alt, "a photo"));

        cut.Markup.Should().Contain("safe-image-blur");
        cut.Markup.Should().Contain("Show image");
    }

    [Fact]
    public void Reveals_OnShowClick()
    {
        var cut = RenderComponent<SafeImage>(p => p
            .Add(x => x.Src, "https://images.example.com/photo.jpg"));

        cut.Find("button.mud-button").Click();

        cut.Markup.Should().NotContain("safe-image-overlay");
        cut.Markup.Should().Contain("https://images.example.com/photo.jpg");
    }

    [Fact]
    public void RendersNothing_ForEmptySrc()
    {
        var cut = RenderComponent<SafeImage>(p => p.Add(x => x.Src, ""));
        cut.Markup.Trim().Should().BeEmpty();
    }
}
