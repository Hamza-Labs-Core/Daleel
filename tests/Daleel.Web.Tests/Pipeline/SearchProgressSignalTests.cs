using Daleel.Web.Pipeline;
using FluentAssertions;
using Xunit;

namespace Daleel.Web.Tests.Pipeline;

/// <summary>
/// The <see cref="SearchProgressSignal"/> wire format is the contract between the server pipeline
/// (which emits step + localization key + args) and the SearchProgress UI (which decodes and localizes
/// in the viewer's culture). These guard the round-trip, backward-compatibility with plain lines, and
/// the framing safety that stops user-supplied args from corrupting the encoding.
/// </summary>
public class SearchProgressSignalTests
{
    [Fact]
    public void Encode_then_Decode_round_trips_step_key_and_args()
    {
        var encoded = SearchProgressSignal.Encode(SearchStep.SearchingWeb, "Progress.Msg.Gathered", 5, 3, 8);

        SearchProgressSignal.TryDecode(encoded, out var signal).Should().BeTrue();
        signal.Step.Should().Be(SearchStep.SearchingWeb);
        signal.Key.Should().Be("Progress.Msg.Gathered");
        signal.Args.Should().Equal("5", "3", "8");
    }

    [Fact]
    public void Encode_with_no_args_decodes_to_an_empty_arg_list()
    {
        var encoded = SearchProgressSignal.Encode(SearchStep.Done, "Progress.Msg.Done");

        SearchProgressSignal.TryDecode(encoded, out var signal).Should().BeTrue();
        signal.Step.Should().Be(SearchStep.Done);
        signal.Key.Should().Be("Progress.Msg.Done");
        signal.Args.Should().BeEmpty();
    }

    [Theory]
    [InlineData("Found 8 stores selling ACs…")]      // a plain agent line
    [InlineData("")]
    [InlineData(null)]
    public void TryDecode_returns_false_for_non_encoded_strings(string? plain)
    {
        SearchProgressSignal.TryDecode(plain, out var signal).Should().BeFalse();
        signal.Should().Be(default(SearchProgressSignal));
    }

    [Fact]
    public void Dollar_prefixed_noun_key_survives_the_round_trip_for_client_side_localization()
    {
        // ParseQueryActivity passes the query-type noun as a "$resource.key" arg; the UI localizes it.
        var encoded = SearchProgressSignal.Encode(
            SearchStep.Analyzing, "Progress.Msg.LookingFor", "$Progress.Noun.Products", "split AC");

        SearchProgressSignal.TryDecode(encoded, out var signal).Should().BeTrue();
        signal.Args.Should().Equal("$Progress.Noun.Products", "split AC");
    }

    [Fact]
    public void Control_chars_in_args_are_stripped_so_they_cannot_corrupt_the_framing()
    {
        // A (pathological) store name containing the delimiter bytes must not split into extra fields.
        var nasty = "Smart\u001fBuy\u0001";
        var encoded = SearchProgressSignal.Encode(SearchStep.FindingStores, "Progress.Msg.VerifyingStore", nasty);

        SearchProgressSignal.TryDecode(encoded, out var signal).Should().BeTrue();
        signal.Args.Should().HaveCount(1);
        signal.Args[0].Should().Be("Smart Buy ");
    }

    [Fact]
    public void Decode_of_an_out_of_range_step_falls_back_safely()
    {
        // A signal from a newer server reporting a step this build doesn't know about must still decode.
        var forged = "\u000199\u001fProgress.Msg.Done";

        SearchProgressSignal.TryDecode(forged, out var signal).Should().BeTrue();
        signal.Step.Should().Be(SearchStep.Analyzing);
        signal.Key.Should().Be("Progress.Msg.Done");
    }

    [Fact]
    public void EncodeWireSafe_strips_the_internal_key_but_keeps_step_and_args()
    {
        // The external SignalR broadcast must not leak internal key names (e.g. "…ScrapingBrandCatalog");
        // off-device subscribers get only the step + user-facing args.
        SearchProgressSignal.TryDecode(
            SearchProgressSignal.Encode(SearchStep.BuildingProfiles, "Progress.Msg.ScrapingBrandCatalog", "Samsung"),
            out var full).Should().BeTrue();

        var wire = SearchProgressSignal.EncodeWireSafe(full);

        wire.Should().NotContain("Progress.Msg").And.NotContain("Scraping").And.NotContain("Catalog");
        SearchProgressSignal.TryDecode(wire, out var decoded).Should().BeTrue();
        decoded.Step.Should().Be(SearchStep.BuildingProfiles);   // step preserved
        decoded.Key.Should().BeEmpty();                          // internal key gone
        decoded.Args.Should().Equal("Samsung");                  // user-facing arg preserved
    }
}
