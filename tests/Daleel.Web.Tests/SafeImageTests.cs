using Bunit;
using Daleel.Web.Components.Shared;
using Daleel.Web.Services;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using Xunit;

namespace Daleel.Web.Tests;

/// <summary>
/// SafeImage shows images by default (hiding inappropriate ones is the HaramBlur extension's job),
/// and only blurs-until-revealed when the user has opted into app-side blur (img.blur = "1").
/// </summary>
public class SafeImageTests : TestContext
{
    public SafeImageTests()
    {
        Services.AddMudServices();
        Services.AddScoped<BrowserStore>();
        JSInterop.Mode = JSRuntimeMode.Loose; // localStorage reads return null ⇒ blur defaults OFF
    }

    [Fact]
    public void Shows_Image_ByDefault()
    {
        var cut = RenderComponent<SafeImage>(p => p
            .Add(x => x.Src, "https://images.example.com/photo.jpg")
            .Add(x => x.Alt, "a photo"));

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("https://images.example.com/photo.jpg");
            cut.Markup.Should().NotContain("safe-image-blur");
            cut.Markup.Should().NotContain("Show image");
        });
    }

    [Fact]
    public void Blurs_WhenUserOptsIn_AndRevealsOnClick()
    {
        // Opt into app-side blur: every localStorage read returns "1" (img.blur ON).
        JSInterop.Setup<string?>("localStorage.getItem", _ => true).SetResult("1");

        var cut = RenderComponent<SafeImage>(p => p
            .Add(x => x.Src, "https://images.example.com/photo.jpg"));

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("safe-image-blur"));

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
