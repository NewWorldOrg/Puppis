namespace Puppis.Architecture.Tests;

public sealed class FileSystemRuleTests
{
    private static readonly string[] Inventory =
    [
        "/Puppis.Driver/CardLending/BorrowedCard.cs .CopyTo(",
        "/Puppis.Driver/CardLending/CardLender.cs .CopyTo(",
        "/Puppis.Driver/CardLending/LendingProtocol.cs .Decrypt(",
        "/Puppis.Driver/CardLending/LendingProtocol.cs .Encrypt(",
        "/Puppis.Driver/Configuration/AtomicFile.cs File.Delete",
        "/Puppis.Driver/Configuration/AtomicFile.cs File.Move",
        "/Puppis.Driver/Configuration/AtomicFile.cs FileMode.",
        "/Puppis.Driver/Configuration/AtomicFile.cs newFileStream",
        "/Puppis.Driver/Configuration/DriverConfigurationReader.cs File.Create",
        "/Puppis.Driver/Configuration/DriverConfigurationReader.cs File.Delete",
        "/Puppis.Driver/Descrambling/CardReaders.cs .Replace(",
        "/Puppis.Driver/Descrambling/PcscLibrary.cs NativeLibrary",
        "/Puppis.Driver/Descrambling/SectionCollector.cs .CopyTo(",
        "/Puppis.Driver/Descrambling/TransportStreamDescrambler.cs .CopyTo(",
        "/Puppis.Driver/Ipc/DriverSocket.cs File.Delete",
        "/Puppis.Driver/Ipc/DriverSocket.cs File.SetUnixFileMode",
        "/Puppis.Driver/Ipc/StorageViews.cs File.Delete",
        "/Puppis.Driver/Ipc/StorageViews.cs FileMode.",
        "/Puppis.Driver/Ipc/UnixFile.cs LibraryImport",
        "/Puppis.Driver/Recording/RecordingEraser.cs File.Delete",
        "/Puppis.Driver/Recording/RecordingWriter.cs .Replace(",
        "/Puppis.Driver/Recording/RecordingWriter.cs FileMode.",
        "/Puppis.Driver/Recording/RecordingWriter.cs newFileStream",
        "/Puppis.Driver/Recording/StrayFileEraser.cs File.Delete",
        "/Puppis.Driver/Tuning/Dvb/DvbSystemCalls.cs LibraryImport",
        "/Puppis.Driver/Tuning/Dvb/DvbTunerDetector.cs .Replace(",
        "/Puppis.Driver/Tuning/TunerLedgerStore.cs .Replace(",
        "/Puppis.Driver/Tuning/Usb/CechZd1/CaptureUnpacker.cs .CopyTo(",
        "/Puppis.Driver/Tuning/Usb/CechZd1/CechZd1TunerDevice.cs .CopyTo(",
        "/Puppis.Driver/Tuning/Usb/LinuxUsbBulkReader.cs .CopyTo(",
        "/Puppis.Driver/Tuning/Usb/LinuxUsbConnection.cs LibraryImport",
        "/Puppis.Driver/Tuning/Usb/LinuxUsbHost.cs LibraryImport",
    ];

    [Fact]
    public void EveryWayThisRepositoryCouldChangeWhatIsOnDiskIsWrittenDownHere()
    {
        Assert.Equal(Inventory, FileSystemRules.WhatCouldChangeWhatIsOnDisk(RepositoryLayout.SourceDirectory));
    }

    [Fact]
    public void TheContractChangesNothingOnDisk()
    {
        Assert.DoesNotContain(Inventory, entry => entry.StartsWith("/Puppis.Contracts/", StringComparison.Ordinal));
    }

    [Fact]
    public void TheOnlyFilesOpenedForWritingAreTheRecordingAndTheLedgerBesideTheConfiguration()
    {
        Assert.Equal(
            [
                "/Puppis.Driver/Configuration/AtomicFile.cs newFileStream",
                "/Puppis.Driver/Recording/RecordingWriter.cs newFileStream",
            ],
            Inventory.Where(entry => entry.EndsWith("newFileStream", StringComparison.Ordinal)).ToArray());
    }

    [Fact]
    public void TheOnlyFilesRemovedUnderAnOutputRootAreARecordingOfItsOwnAndAFileNobodyOwns()
    {
        Assert.Equal(
            [
                "/Puppis.Driver/Recording/RecordingEraser.cs File.Delete",
                "/Puppis.Driver/Recording/StrayFileEraser.cs File.Delete",
            ],
            Inventory
                .Where(entry => entry.StartsWith("/Puppis.Driver/Recording/", StringComparison.Ordinal))
                .Where(entry => entry.EndsWith("File.Delete", StringComparison.Ordinal))
                .ToArray());
    }

    [Fact]
    public void TheDriverReachesNativeCodeForTheCardReaderTheTunerTheUsbHostAndTheSocketFileOnly()
    {
        Assert.Equal(
            [
                "/Puppis.Driver/Descrambling/PcscLibrary.cs NativeLibrary",
                "/Puppis.Driver/Ipc/UnixFile.cs LibraryImport",
                "/Puppis.Driver/Tuning/Dvb/DvbSystemCalls.cs LibraryImport",
                "/Puppis.Driver/Tuning/Usb/LinuxUsbConnection.cs LibraryImport",
                "/Puppis.Driver/Tuning/Usb/LinuxUsbHost.cs LibraryImport",
            ],
            Inventory
                .Where(entry => entry.EndsWith("LibraryImport", StringComparison.Ordinal)
                    || entry.EndsWith("NativeLibrary", StringComparison.Ordinal)
                    || entry.EndsWith("DllImport", StringComparison.Ordinal))
                .ToArray());
    }

    [Fact]
    public void TheDriverStartsNoProgramme()
    {
        Assert.DoesNotContain(Inventory, entry => entry.Contains("Process", StringComparison.Ordinal));
    }

    [Fact]
    public void TheReplaceInTheRecordingWriterRewordsAFailureAndMovesNoFile()
    {
        string source = File.ReadAllText(Path.Combine(
            RepositoryLayout.SourceDirectory,
            "Puppis.Driver",
            "Recording",
            "RecordingWriter.cs"));

        Assert.Equal(
            [".Replace(", "FileMode.", "newFileStream"],
            FileSystemRules.WhatCouldChangeWhatIsOnDiskIn(source));
        Assert.Contains("error.Message.Replace(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("File.Replace(", source, StringComparison.Ordinal);
    }
}
