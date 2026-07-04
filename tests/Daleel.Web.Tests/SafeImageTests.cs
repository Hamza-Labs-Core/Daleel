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
    public void BlurKillSwitch_IgnoresOptIn_ImageStaysVisible()
    {
        // All user-facing blur UI is suspended (SafeImage.BlurUiEnabled = false) until the
        // confidence-scored moderation system is user-ready: even a user who opted into
        // app-side blur (img.blur = "1") must see the image plainly, with no overlay.
        // When re-enabling the kill-switch, restore the original expectation:
        // opt-in → "safe-image-blur" appears → Reveal click clears the overlay.
        JSInterop.Setup<string?>("localStorage.getItem", _ => true).SetResult("1");

        var cut = RenderComponent<SafeImage>(p => p
            .Add(x => x.Src, "https://images.example.com/photo.jpg"));

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("https://images.example.com/photo.jpg");
            cut.Markup.Should().NotContain("safe-image-blur");
            cut.Markup.Should().NotContain("safe-image-overlay");
        });
    }

    [Fact]
    public void Shows_Image_EvenWhenJsInteropUnavailable()
    {
        // Simulate prerender: JS interop throws "calls cannot be issued at this time" (an
        // InvalidOperationException), exactly as during static SSR. BrowserStore swallows it and the image
        // must still render — the reveal must never hinge on a client round-trip succeeding. That fail-closed
        // default was the "every image stuck behind Image hidden" bug.
        JSInterop.Setup<string?>("localStorage.getItem", _ => true)
            .SetException(new InvalidOperationException("JavaScript interop calls cannot be issued at this time."));

        var cut = RenderComponent<SafeImage>(p => p
            .Add(x => x.Src, "https://images.example.com/photo.jpg"));

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("https://images.example.com/photo.jpg");
            cut.Markup.Should().NotContain("safe-image-overlay");
            cut.Markup.Should().NotContain("safe-image-blur");
        });
    }

    [Fact]
    public void RendersNothing_ForEmptySrc()
    {
        var cut = RenderComponent<SafeImage>(p => p.Add(x => x.Src, ""));
        cut.Markup.Trim().Should().BeEmpty();
    }
}
