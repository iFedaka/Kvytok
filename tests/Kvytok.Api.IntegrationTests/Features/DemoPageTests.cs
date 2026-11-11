using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Kvytok.Api.Features.GetEvent;
using Kvytok.Api.Features.ListEvents;
using Kvytok.Api.Features.ReserveSeat;
using Kvytok.Domain;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Kvytok.Api.IntegrationTests.Features;

public sealed class DemoPageTests(ApiFixture api)
{
    [Fact]
    public async Task Serves_The_Seat_Grid()
    {
        var response = await api.Client.GetAsync("/demo/", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        var html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("id=\"grid\"", html);
    }

    [Fact]
    public async Task Redirects_The_Bare_Path_To_The_Page()
    {
        var response = await api.Client.GetAsync("/demo", TestContext.Current.CancellationToken);

        // the test client follows redirects, so landing on the page is the proof
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("/demo/", response.RequestMessage!.RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task Serves_Nothing_Else_In_Wwwroot()
    {
        // The decoy is real, so the 404 means "not served", not "not there". Static files are
        // rooted at wwwroot/demo; a bare UseStaticFiles() would serve the whole folder, and this
        // is what would notice.
        var webRoot = api.Services.GetRequiredService<IWebHostEnvironment>().WebRootPath;
        Assert.True(File.Exists(Path.Combine(webRoot, "not-served.txt")));

        var response = await api.Client.GetAsync("/not-served.txt", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // The JSON names the page reads and writes, taken from the records that produce them
    public static TheoryData<string> Fields => new(
        new[]
        {
            nameof(ListEventsResponse.Events),
            nameof(EventSummary.EventId),
            nameof(EventSummary.Name),
            nameof(EventSummary.StartsAt),
            nameof(EventSummary.SeatCount),
            nameof(GetEventResponse.Name),
            nameof(GetEventResponse.StartsAt),
            nameof(GetEventResponse.SeatCount),
            nameof(GetEventResponse.FreeSeatCount),
            nameof(GetEventResponse.Seats),
            nameof(ReserveSeatRequest.SeatNumber),
            nameof(ReserveSeatRequest.HolderEmail),
            nameof(ReserveSeatResponse.ExpiresAt),
        }.Select(JsonNamingPolicy.CamelCase.ConvertName).Distinct());

    [Theory]
    [MemberData(nameof(Fields))]
    public async Task Uses_The_Field_Names_The_Api_Serializes(string field)
    {
        // The page repeats the contract in JavaScript, where the compiler cannot see it. Renamed on
        // the C# side, a field the page reads comes back undefined and the grid draws wrong with no
        // error anywhere, so the names are pinned here instead. Proves each name is on the page,
        // not that every use of it is right.
        //
        // Matched only in the script, comments removed, and only where JavaScript uses a field: read
        // as .field, written as field:, or destructured as { field }. A bare word would be satisfied by
        // markup (<meta name>, role="status", class="events") and by a renamed field that collides
        // with one, such as Name → Title against <title>.
        var script = await ScriptAsync();

        Assert.Matches(new Regex($@"\.{field}\b|\b{field}\s*:|\{{\s*{field}\s*\}}"), script);
    }

    [Theory]
    [InlineData(nameof(SeatDto.Number))]
    [InlineData(nameof(SeatDto.Status))]
    public async Task Reads_The_Seat_Fields_The_Api_Serializes(string field)
    {
        // A seat's fields are read off s, the page's name for a seat. Pinned to it because .status
        // alone is also the fetch response's status, which would pass with no seat read at all.
        var script = await ScriptAsync();

        Assert.Matches(new Regex($@"\bs\.{JsonNamingPolicy.CamelCase.ConvertName(field)}\b"), script);
    }

    [Theory]
    [InlineData(nameof(SeatStatus.Free))]
    [InlineData(nameof(SeatStatus.Held))]
    [InlineData(nameof(SeatStatus.Booked))]
    public async Task Compares_Seat_Status_With_The_Names_The_Api_Serializes(string status)
    {
        // Statuses go out as names (JsonStringEnumConverter); renamed, every seat would draw as taken
        var script = await ScriptAsync();

        Assert.Contains($"\"{status}\"", script);
    }

    // The page's one inline script, as served, with its line comments removed: they name fields too
    private async Task<string> ScriptAsync()
    {
        var html = await api.Client.GetStringAsync("/demo/", TestContext.Current.CancellationToken);
        var script = Regex.Match(html, @"<script>(.*?)</script>", RegexOptions.Singleline);
        Assert.True(script.Success, "no inline <script> on the page");
        return Regex.Replace(script.Groups[1].Value, @"(^|\s)//[^\n]*", "$1");
    }
}
