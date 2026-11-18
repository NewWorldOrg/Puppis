using System.Net;
using System.Text.Json;

using Microsoft.Extensions.Hosting;

using Puppis.Contracts;
using Puppis.Driver.Configuration;
using Puppis.Driver.Ipc;
using Puppis.Driver.Reception;

namespace Puppis.Driver.Tests;

public sealed class ReceptionApiTests
{
    private static async Task<JsonElement> ReceptionAsync(HttpClient client)
    {
        using HttpResponseMessage response = await client.GetAsync(ReceptionViews.Path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        return body.RootElement.Clone();
    }

    private static async Task UseOnceAsync(HttpClient client, string sessionId)
    {
        using HttpResponseMessage started = await client.PostAsync(
            DriverEndpoints.Sessions,
            DriverUnderTest.Body(DriverUnderTest.Live(sessionId))
        );

        Assert.Equal(HttpStatusCode.Created, started.StatusCode);

        using HttpResponseMessage stopped = await client.DeleteAsync(
            $"{DriverEndpoints.Session(SessionId.Parse(sessionId))}?reason=test"
        );

        Assert.True(stopped.IsSuccessStatusCode, stopped.StatusCode.ToString());
    }

    [Fact]
    public void TheRouteIsBesideTheTunersAndIsNotOneOfTheEndpointsOfTheContract()
    {
        Assert.Equal("/tuners/reception", ReceptionViews.Path);
        Assert.DoesNotContain(ReceptionViews.Path, DriverEndpoints.All);
    }

    [Fact]
    public async Task ADriverThatHasReceivedNothingListsItsTunersWithNoRecords()
    {
        await using DriverUnderTest driver = await DriverUnderTest.Start();
        using HttpClient client = driver.Client();

        JsonElement reception = await ReceptionAsync(client);

        Assert.Equal(0, reception.GetProperty("knownChannels").GetArrayLength());
        Assert.Equal(
            ["fake-terrestrial", "fake-satellite", "fake-spare"],
            reception.GetProperty("tuners").EnumerateArray().Select(tuner => tuner.GetProperty("deviceId").GetString())
        );
        Assert.All(
            reception.GetProperty("tuners").EnumerateArray(),
            tuner => Assert.Equal(0, tuner.GetProperty("channels").GetArrayLength())
        );
    }

    [Fact]
    public async Task ASessionThatEndedShowsUpAsAResultOfItsTunerAndItsChannelBecomesKnown()
    {
        await using DriverUnderTest driver = await DriverUnderTest.Start();
        using HttpClient client = driver.Client();

        await UseOnceAsync(client, "viewer");

        JsonElement reception = await ReceptionAsync(client);
        JsonElement known = Assert.Single(reception.GetProperty("knownChannels").EnumerateArray());
        JsonElement tuner = reception.GetProperty("tuners").EnumerateArray().First();
        JsonElement channel = Assert.Single(tuner.GetProperty("channels").EnumerateArray());

        Assert.Equal("terrestrial", known.GetProperty("kind").GetString());
        Assert.Equal(55, known.GetProperty("physicalChannel").GetInt32());
        Assert.Equal(0, known.GetProperty("transportStreamId").GetInt32());
        Assert.Equal("fake-terrestrial", tuner.GetProperty("deviceId").GetString());
        Assert.Equal("terrestrial", channel.GetProperty("kind").GetString());
        Assert.Equal(55, channel.GetProperty("physicalChannel").GetInt32());
        Assert.Equal(1, channel.GetProperty("results").GetInt32());
        Assert.Equal(1, channel.GetProperty("locked").GetInt32());
        Assert.True(channel.TryGetProperty("carrierToNoiseDecibels", out _));
        Assert.True(channel.GetProperty("newestAt").GetDateTimeOffset() > DateTimeOffset.UtcNow.AddMinutes(-5));
    }

    [Fact]
    public async Task WhatTheDriverLearnedIsThereAgainAfterARestartOnTheSameStateDirectory()
    {
        DriverUnderTest.ClearTheInheritedUrls();
        string root = DriverUnderTest.NewRoot();
        string state = Directory.CreateDirectory(Path.Combine(root, "state")).FullName;
        DriverConfiguration configuration = DriverUnderTest.ConfigurationIn(root);

        try
        {
            using (IHost first = Host(configuration, state))
            {
                await first.StartAsync();
                ReceptionBook learned = (ReceptionBook)first.Services.GetService(typeof(ReceptionBook))!;
                learned.Note(
                    "fake-terrestrial",
                    new ReceptionChannel(TunerKind.Terrestrial, 31),
                    new ReceptionResult(DateTimeOffset.UnixEpoch, true, 21.5),
                    () => false
                );
                await first.StopAsync(TimeSpan.FromSeconds(20));
            }

            Assert.True(File.Exists(Path.Combine(state, ReceptionStore.FileName)));

            using IHost second = Host(configuration, state);
            ReceptionBook restored = (ReceptionBook)second.Services.GetService(typeof(ReceptionBook))!;

            Assert.Equal(
                new ReceptionStanding(1, 1, 21.5, DateTimeOffset.UnixEpoch),
                restored.StandingOf("fake-terrestrial", new ReceptionChannel(TunerKind.Terrestrial, 31))
            );
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task TheDriverMeasuresItsIdleTunersUnlessToldNotTo()
    {
        await using DriverUnderTest measuring = await DriverUnderTest.Start();
        await using DriverUnderTest quiet = await DriverUnderTest.Start(
            reshape: given => given with { Tuner = given.Tuner! with { MeasureWhileIdle = false } }
        );

        Assert.Contains(measuring.Service<IEnumerable<IHostedService>>(), service => service is IdleReceptionMeasurer);
        Assert.Null(quiet.Service<IdleReceptionMeasurer>().MeasureIfDue());
    }

    private static IHost Host(DriverConfiguration configuration, string state)
    {
        DriverHostResult built = DriverHost.Create([], configuration, stateDirectory: state);

        Assert.True(built.TryGetHost(out IHost? host), string.Join(" ", built.Problems));

        return host;
    }
}
