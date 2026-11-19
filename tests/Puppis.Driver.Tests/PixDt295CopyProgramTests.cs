using System.Buffers.Binary;

using Puppis.Driver.Tuning.Dvb;
using Puppis.Driver.Tuning.Usb;
using Puppis.Driver.Tuning.Usb.Bridge;
using Puppis.Driver.Tuning.Usb.PixDt295;

namespace Puppis.Driver.Tests;

public sealed class CopyProgramTests
{
    private static readonly uint[] TriedOnTheDevice =
    [
        0xC46D02B0, 0xC7ECAAC0, 0xE2039BED, 0x117E970D, 0xC48D01B0, 0xC40CAAC0, 0xE2039BED, 0x117E970D,
        0x04B09BCC, 0x4620AAC0, 0xE2039BED, 0x117E970D, 0x8BC78BB2, 0xA4428B73, 0xC1040420, 0x1430A442,
        0xA443A5F4, 0xE3FAC003, 0xC0CD0076, 0xC000C14D, 0x10700470, 0x9F814454, 0x3935AA10, 0xE372C08D,
        0x0070A810, 0xE26CA820, 0xE36C9F81, 0x00600000, 0xAA16E567, 0x9B0001FF, 0x8260E363, 0xC10D0075,
        0xA805E25F, 0x9B000800, 0xAA05EF5B, 0x9F8C00FD, 0x814C04C0, 0xA8F0E355, 0x9F810190, 0x20100410,
        0x9F810200, 0x0000AA10, 0xE3509B34, 0xE800A5F4, 0xE2469F81, 0x01902028, 0x0412C14D, 0x10729F81,
        0x00600000, 0xAA12E53F, 0x8B20AC60, 0xE501A610, 0x9B0C0200, 0xAAC0E503, 0xA803E331, 0xE0E89F8C,
        0x00FDBD0C, 0x04C0C041, 0x8210E2E1, 0x9F828138, 0xEC00A662, 0x9F8C00FD, 0xBD84C801, 0x042014C0,
        0xA442A5F1, 0xE3FB9F8C, 0x00FD814C, 0xC08014C0, 0x9F8100FD, 0xBD0CC040, 0x1410C0F0, 0x14C09B00,
        0x0200A606, 0x9F810060, 0x0000AA16, 0xE301C006, 0x9F810190, 0x202C1416, 0xA413A5F5, 0xE3B8C000,
        0xE005C010, 0xE003C020, 0xE001C030, 0xC18D1070, 0xC0CD1076, 0xC10D1073, 0xC08D0070, 0x9F818000,
        0x00009210, 0x10708B72, 0xC1C0A602, 0xC000C091, 0x1420A442, 0xA5F1E3FC, 0x9BECA8C8, 0x970CC46D,
        0x02B0A810, 0xE2039BEC, 0xA75A970C, 0x9BECA704, 0x970C9FA0,
    ];

    [Fact]
    public void TheProgramAssembledFromItsBehaviourIsTheOneTriedOnTheDevice()
    {
        Fr30Program program = CopyProgram.Build();

        Assert.Equal(CopyProgram.Entry, program.Origin);
        Assert.Equal(TriedOnTheDevice, program.Words);
    }

    [Fact]
    public void TheHookLoadsTheEntryIntoR12AndJumpsThroughIt()
    {
        Assert.Equal([0x9BEC1000u, 0x970C9FA0u], CopyProgram.Hook);
    }

    [Fact]
    public void TheProgramFitsThePlaceLeftFreeForIt()
    {
        Assert.True(CopyProgram.Build().Words.Count * 4 <= CopyProgram.PlaceBytes);
    }

    [Fact]
    public void AnInstructionIsSixteenBigEndianWordsWithItsMarkInTheValue()
    {
        byte[] instruction = CopyProgram.Instruction(7, CopyCommand.Copy, 0x200, 2048);

        Assert.Equal(64, instruction.Length);
        Assert.Equal(CopyProgram.Magic, BinaryPrimitives.ReadUInt32BigEndian(instruction));
        Assert.Equal(7u, BinaryPrimitives.ReadUInt32BigEndian(instruction.AsSpan(4)));
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32BigEndian(instruction.AsSpan(8)));
        Assert.Equal(0x200u, BinaryPrimitives.ReadUInt32BigEndian(instruction.AsSpan(12)));
        Assert.Equal(2048u, BinaryPrimitives.ReadUInt32BigEndian(instruction.AsSpan(16)));
        Assert.All(instruction[20..], value => Assert.Equal(0, value));
        Assert.Equal(new UsbSetup(0x40, 0xBC, 0x0C7E, 0x4620), CopyProgram.InstructionSetup);
    }

    [Fact]
    public void AnAnswerToAnotherInstructionIsRefused()
    {
        uint[] words = [CopyProgram.Magic, 4, 0x80000002, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];

        Assert.Throws<DvbDeviceException>(() => CopyAnswer.From(words, 5, CopyCommand.Copy, "/dev/x"));
        Assert.Throws<DvbDeviceException>(() => CopyAnswer.From(words, 4, CopyCommand.Answer, "/dev/x"));
        words[0] = 0;
        Assert.Throws<DvbDeviceException>(() => CopyAnswer.From(words, 4, CopyCommand.Copy, "/dev/x"));
    }
}

/// <summary>
/// The copying program run on an FR30 machine against a simulated bridge, through the hook, as the bridge would run it
/// for each memory write.
/// </summary>
public sealed class CopyProgramRunTests
{
    private static (FakeDt295 Device, BridgeMemory Memory) Hooked()
    {
        FakeDt295 device = FakeDt295.Locking();
        Fr30Program program = CopyProgram.Build();

        for (int word = 0; word < program.Words.Count; word++)
        {
            device.Word(CopyProgram.Entry + (uint)(4 * word), program.Words[word]);
        }

        device.Word(CopyProgram.HookAt, CopyProgram.Hook[0]);
        device.Word(CopyProgram.HookAt + 4, CopyProgram.Hook[1]);

        return (device, new BridgeMemory(new TurnTakingConnection(device)));
    }

    private static uint[] Send(FakeDt295 device, byte[] instruction)
    {
        device.ControlOut(CopyProgram.InstructionSetup, instruction, TimeSpan.FromSeconds(2));
        byte[] answer = new byte[64];
        device.ControlIn(new UsbSetup(0xC0, 0xBC, 0x0C00, 0x4620), answer, TimeSpan.FromSeconds(1));

        return [.. Enumerable.Range(0, 16).Select(word => BinaryPrimitives.ReadUInt32BigEndian(answer.AsSpan(4 * word)))];
    }

    private static void Streaming(FakeDt295 device) => device.Word(CopyProgram.CaptureRunning, CopyProgram.Running);

    [Fact]
    public void APlainWriteGoesOnAsTheReplacedWordsWouldHaveLeftIt()
    {
        (FakeDt295 device, BridgeMemory memory) = Hooked();

        memory.Write(0xFD80C4, 0x12345678);

        Assert.Equal([CopyProgram.OriginalWrite], device.PassThroughExits);
        Assert.Equal(0x12345678u, device.Word(0xFD80C4));
        uint[] left = device.RegistersLeft!;
        Assert.Equal(0x46u, left[13]);
        Assert.Equal(0u, left[0]);
        Assert.False(device.FlagsLeftEqual);

        foreach (int untouched in (int[])[1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 14, 15])
        {
            Assert.Equal(FakeDt295.Sentinels[untouched], left[untouched]);
        }
    }

    [Fact]
    public void AWriteThatModifiesGoesOnToItsOwnHandlingWithTheFlagsSaying()
    {
        (FakeDt295 device, _) = Hooked();

        device.ControlOut(new UsbSetup(0x40, 0xBC, 0xFD01, 0x80C4), [0, 0, 0, 1], TimeSpan.FromSeconds(1));

        Assert.Equal([CopyProgram.OriginalModify], device.PassThroughExits);
        Assert.Equal(1u, device.RegistersLeft![0]);
        Assert.True(device.FlagsLeftEqual);
    }

    [Theory]
    [InlineData(0x0C7E, 0x4620, 60)]
    [InlineData(0x0C7E, 0x4624, 64)]
    [InlineData(0x0D7E, 0x4620, 64)]
    public void AMarkedWriteThatIsNotAnInstructionIsHandedBack(int value, int index, int length)
    {
        (FakeDt295 device, _) = Hooked();

        device.ControlOut(new UsbSetup(0x40, 0xBC, (ushort)value, (ushort)index), new byte[length], TimeSpan.FromSeconds(1));

        Assert.Equal([CopyProgram.OriginalWrite], device.PassThroughExits);
        Assert.Equal(0, device.Instructions);
    }

    [Fact]
    public void AnInstructionToAnswerIsAnsweredWithoutCopying()
    {
        (FakeDt295 device, _) = Hooked();

        uint[] answer = Send(device, CopyProgram.Instruction(9, CopyCommand.Answer, 0x400, 5));

        Assert.Equal([CopyProgram.Magic, 9u, 0x80000001u, 0x400u, 0u, 0u, (uint)CopyResult.Good], answer[..7]);
        Assert.All(answer[7..], word => Assert.Equal(0u, word));
        Assert.Empty(device.PassThroughExits);
    }

    [Theory]
    [InlineData(0x11111111u, 2u, 0u, 1u)]
    [InlineData(CopyProgram.Magic, 3u, 0u, 1u)]
    [InlineData(CopyProgram.Magic, 2u, 0x600000u, 1u)]
    [InlineData(CopyProgram.Magic, 2u, 0x100u, 1u)]
    [InlineData(CopyProgram.Magic, 2u, 0u, 0u)]
    [InlineData(CopyProgram.Magic, 2u, 0u, 2049u)]
    public void AnInstructionThatIsNotRightIsRefused(uint magic, uint command, uint position, uint limit)
    {
        (FakeDt295 device, _) = Hooked();
        Streaming(device);
        byte[] instruction = CopyProgram.Instruction(3, (CopyCommand)command, position, limit);
        BinaryPrimitives.WriteUInt32BigEndian(instruction, magic);

        uint[] answer = Send(device, instruction);

        Assert.Equal((uint)CopyResult.Wrong, answer[6]);
        Assert.Equal(0u, answer[4]);
    }

    [Fact]
    public void AnInstructionWhileTheUsbCoreIsBusyElsewhereIsRefused()
    {
        (FakeDt295 device, _) = Hooked();
        Streaming(device);
        device.Word(CopyProgram.UsbGroup, 6);

        uint[] answer = Send(device, CopyProgram.Instruction(1, CopyCommand.Copy, 0, 8));

        Assert.Equal((uint)CopyResult.Wrong, answer[6]);
    }

    [Fact]
    public void AnInstructionWhileNothingStreamsSaysSo()
    {
        (FakeDt295 device, _) = Hooked();

        uint[] answer = Send(device, CopyProgram.Instruction(1, CopyCommand.Copy, 0, 8));

        Assert.Equal((uint)CopyResult.NotStreaming, answer[6]);
    }

    [Fact]
    public void AWriterOutsideTheRingIsReported()
    {
        (FakeDt295 device, _) = Hooked();
        Streaming(device);
        device.Word(CopyProgram.WriterPosition, CopyProgram.RingBytes);

        uint[] answer = Send(device, CopyProgram.Instruction(1, CopyCommand.Copy, 0, 8));

        Assert.Equal((uint)CopyResult.WriterOutsideTheRing, answer[6]);
        Assert.Equal(CopyProgram.RingBytes, answer[5]);
    }

    [Fact]
    public void WhatTheRingHoldsIsCopiedChunkByChunkUpToTheLimit()
    {
        (FakeDt295 device, _) = Hooked();
        Streaming(device);
        device.Broadcast(16);
        var reader = (IUsbBulkReader)device.ReadBulk(0x81, 512, 1);

        uint[] answer = Send(device, CopyProgram.Instruction(2, CopyCommand.Copy, 0, 4));

        Assert.Equal((uint)CopyResult.Good, answer[6]);
        Assert.Equal(4u, answer[4]);
        Assert.Equal(2048u, answer[3]);
        Assert.Equal(16u * 192, answer[5]);
        Assert.Equal(2048u, device.Word(CopyProgram.ReaderPosition));
        byte[] chunk = new byte[512];
        Assert.Equal(512, reader.Take(chunk, TimeSpan.Zero));
        Assert.Equal(0x47, chunk[4]);
        Assert.Equal(0x47, chunk[196]);
    }

    [Fact]
    public void CopyingStopsAtTheLastWholeChunkTheWriterHasPassed()
    {
        (FakeDt295 device, _) = Hooked();
        Streaming(device);
        device.Broadcast(6);

        uint[] answer = Send(device, CopyProgram.Instruction(2, CopyCommand.Copy, 0, 2048));

        Assert.Equal((uint)CopyResult.Good, answer[6]);
        Assert.Equal(2u, answer[4]);
        Assert.Equal(1024u, answer[3]);
    }

    [Fact]
    public void ReadingWrapsFromTheEndOfTheRingToItsStart()
    {
        (FakeDt295 device, _) = Hooked();
        Streaming(device);
        device.Word(CopyProgram.WriterPosition, 1024);
        uint last = CopyProgram.RingBytes - 512;

        uint[] answer = Send(device, CopyProgram.Instruction(2, CopyCommand.Copy, last, 2048));

        Assert.Equal((uint)CopyResult.Good, answer[6]);
        Assert.Equal(3u, answer[4]);
        Assert.Equal(1024u, answer[3]);
    }

    [Fact]
    public void WithNothingToCopyTheProgramGivesUpAfterItsBudgetOfLooks()
    {
        (FakeDt295 device, _) = Hooked();
        Streaming(device);

        uint[] answer = Send(device, CopyProgram.Instruction(2, CopyCommand.Copy, 0, 2048));

        Assert.Equal((uint)CopyResult.Good, answer[6]);
        Assert.Equal(0u, answer[4]);
        Assert.Equal(0u, answer[3]);
    }

    [Fact]
    public void AnEndpointThatTakesNothingStopsTheCopyingWithinTheBudget()
    {
        (FakeDt295 device, _) = Hooked();
        Streaming(device);
        device.Broadcast(16);
        device.Accepting = false;

        uint[] answer = Send(device, CopyProgram.Instruction(2, CopyCommand.Copy, 0, 2048));

        Assert.Equal((uint)CopyResult.Good, answer[6]);
        Assert.Equal(0u, answer[4]);
    }
}
