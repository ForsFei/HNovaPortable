using System.IO;

namespace HNovaPortable.Services.Scanning;

/// <summary>轻量 PE 头解析：只取判断“图形界面还是控制台”所需的字段。</summary>
public readonly record struct PeInfo(bool IsValid, ushort Machine, int Subsystem)
{
    public const int SubsystemGui = 2;
    public const int SubsystemConsole = 3;

    public bool IsConsole => IsValid && Subsystem == SubsystemConsole;

    public static PeInfo Read(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, FileOptions.RandomAccess);
            using var reader = new BinaryReader(stream);

            if (stream.Length < 0x40)
            {
                return new PeInfo(false, 0, 0);
            }

            stream.Position = 0x3C;
            var peOffset = reader.ReadInt32();
            if (peOffset <= 0 || peOffset + 24 > stream.Length)
            {
                return new PeInfo(false, 0, 0);
            }

            stream.Position = peOffset;
            if (reader.ReadUInt32() != 0x00004550) // "PE\0\0"
            {
                return new PeInfo(false, 0, 0);
            }

            var machine = reader.ReadUInt16();
            stream.Position = peOffset + 20; // 跳过 COFF 头其它字段
            var sizeOfOptionalHeader = reader.ReadUInt16();

            if (sizeOfOptionalHeader < 70 || peOffset + 24 + 70 > stream.Length)
            {
                return new PeInfo(true, machine, 0);
            }

            stream.Position = peOffset + 24 + 68; // OptionalHeader.Subsystem
            var subsystem = reader.ReadUInt16();
            return new PeInfo(true, machine, subsystem);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or EndOfStreamException)
        {
            return new PeInfo(false, 0, 0);
        }
    }
}
