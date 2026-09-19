using System;
using System.IO;
using System.Linq;
using System.Reflection.PortableExecutable;
using LeXtudio.Metadata.Mutable;
using Xunit;

namespace LeXtudio.Metadata.Mutable.Tests
{
    /// <summary>
    /// Regression tests for the Win32 <c>.rsrc</c> relocation logic used when rewriting a PE.
    /// Leaf <c>IMAGE_RESOURCE_DATA_ENTRY.OffsetToData</c> fields are absolute RVAs and must be
    /// shifted by the section relocation delta. Various toolchains lay out the resource
    /// directory, the data entries and the data blobs differently, so the patcher must not
    /// depend on that layout (see lextudio/AssemblyTools#4).
    /// </summary>
    public class RawWin32ResourceSectionTests
    {
        private const int OriginalRva = 0x4000;
        private const int NewRva = 0x8000;

        [Fact]
        public void Patch_ContiguousEntriesThenData_ShiftsAbsoluteRvAs()
        {
            // link.exe / cvtres layout: all IMAGE_RESOURCE_DATA_ENTRY structs are contiguous,
            // followed by the data blobs.
            var bytes = new byte[0x48];
            WriteDirectory(bytes, 0, idCount: 2);
            WriteDirectoryEntry(bytes, 0x10, id: 1, offsetToDataOrDirectory: 0x20);
            WriteDirectoryEntry(bytes, 0x18, id: 2, offsetToDataOrDirectory: 0x30);
            WriteDataEntry(bytes, 0x20, OriginalRva + 0x40, size: 4);
            WriteDataEntry(bytes, 0x30, OriginalRva + 0x44, size: 4);

            RawWin32ResourceSectionBuilder.PatchResourceDataEntries(bytes, OriginalRva, NewRva);

            Assert.Equal((uint)(NewRva + 0x40), ReadU32(bytes, 0x20));
            Assert.Equal((uint)(NewRva + 0x44), ReadU32(bytes, 0x30));
        }

        [Fact]
        public void Patch_InterleavedEntryAndData_ShiftsAbsoluteRvAs()
        {
            // Mono / Xamarin layout variant: each IMAGE_RESOURCE_DATA_ENTRY is immediately
            // followed by its own data blob. The old section-RVA inference used the last data
            // entry (max leaf offset) and therefore computed a wrong delta for this layout.
            var bytes = new byte[0x48];
            WriteDirectory(bytes, 0, idCount: 2);
            WriteDirectoryEntry(bytes, 0x10, id: 1, offsetToDataOrDirectory: 0x20);
            WriteDirectoryEntry(bytes, 0x18, id: 2, offsetToDataOrDirectory: 0x34);
            WriteDataEntry(bytes, 0x20, OriginalRva + 0x30, size: 4);
            WriteDataEntry(bytes, 0x34, OriginalRva + 0x44, size: 4);

            RawWin32ResourceSectionBuilder.PatchResourceDataEntries(bytes, OriginalRva, NewRva);

            Assert.Equal((uint)(NewRva + 0x30), ReadU32(bytes, 0x20));
            Assert.Equal((uint)(NewRva + 0x44), ReadU32(bytes, 0x34));
        }

        [Fact]
        public void Patch_DataRegionWithAlignmentPadding_ShiftsAbsoluteRvAs()
        {
            // Xamarin.Mac.Tasks.dll style: contiguous entries, then a gap before the first
            // data blob (8-byte alignment), so max(leafOffset)+16 is not the data base either.
            var bytes = new byte[0x58];
            WriteDirectory(bytes, 0, idCount: 2);
            WriteDirectoryEntry(bytes, 0x10, id: 1, offsetToDataOrDirectory: 0x20);
            WriteDirectoryEntry(bytes, 0x18, id: 2, offsetToDataOrDirectory: 0x30);
            WriteDataEntry(bytes, 0x20, OriginalRva + 0x48, size: 4);
            WriteDataEntry(bytes, 0x30, OriginalRva + 0x54, size: 4);

            RawWin32ResourceSectionBuilder.PatchResourceDataEntries(bytes, OriginalRva, NewRva);

            Assert.Equal((uint)(NewRva + 0x48), ReadU32(bytes, 0x20));
            Assert.Equal((uint)(NewRva + 0x54), ReadU32(bytes, 0x30));
        }

        [Fact]
        public void Patch_NestedDirectory_UpdatesNestedLeaves()
        {
            // root -> type sub-directory -> language leaf, mirroring a real resource tree.
            var bytes = new byte[0x50];
            WriteDirectory(bytes, 0, idCount: 1);
            WriteDirectoryEntry(bytes, 0x10, id: 1, offsetToDataOrDirectory: 0x80000018);
            WriteDirectory(bytes, 0x18, idCount: 1);
            WriteDirectoryEntry(bytes, 0x28, id: 1, offsetToDataOrDirectory: 0x38);
            WriteDataEntry(bytes, 0x38, OriginalRva + 0x48, size: 4);

            RawWin32ResourceSectionBuilder.PatchResourceDataEntries(bytes, OriginalRva, NewRva);

            Assert.Equal((uint)(NewRva + 0x48), ReadU32(bytes, 0x38));
        }

        [Fact]
        public void Patch_WhenSectionDoesNotMove_LeavesBytesUntouched()
        {
            var bytes = BuildSingleLeafSection(OriginalRva + 0x20);
            var snapshot = (byte[])bytes.Clone();

            RawWin32ResourceSectionBuilder.PatchResourceDataEntries(bytes, OriginalRva, OriginalRva);

            Assert.Equal(snapshot, bytes);
        }

        [Fact]
        public void Patch_WhenOriginalRvaUnknown_LeavesBytesUntouched()
        {
            var bytes = BuildSingleLeafSection(OriginalRva + 0x20);
            var snapshot = (byte[])bytes.Clone();

            RawWin32ResourceSectionBuilder.PatchResourceDataEntries(bytes, 0, NewRva);

            Assert.Equal(snapshot, bytes);
        }

        [Fact]
        public void ResolveSectionRva_ReturnsContainingSectionBase()
        {
            var path = typeof(RawWin32ResourceSectionTests).Assembly.Location;
            using var stream = File.OpenRead(path);
            using var pe = new PEReader(stream);

            var headers = pe.PEHeaders;
            var text = headers.SectionHeaders.First(s => s.Name == ".text");
            Assert.Equal(text.VirtualAddress, MutableAssemblyWriter.ResolveSectionRva(headers, text.VirtualAddress + 1));
            Assert.Equal(0, MutableAssemblyWriter.ResolveSectionRva(headers, 0x7FFFFFFF));
            Assert.Equal(0, MutableAssemblyWriter.ResolveSectionRva(null!, 0));
        }

        private static byte[] BuildSingleLeafSection(int dataRva)
        {
            var bytes = new byte[0x30];
            WriteDirectory(bytes, 0, idCount: 1);
            WriteDirectoryEntry(bytes, 0x10, id: 1, offsetToDataOrDirectory: 0x20);
            WriteDataEntry(bytes, 0x20, dataRva, size: 4);
            return bytes;
        }

        private static void WriteDirectory(byte[] bytes, int offset, ushort idCount, ushort namedCount = 0)
        {
            WriteU32(bytes, offset + 0, 0);          // Characteristics
            WriteU32(bytes, offset + 4, 0);          // TimeDateStamp
            WriteU16(bytes, offset + 8, 0);          // MajorVersion
            WriteU16(bytes, offset + 10, 0);         // MinorVersion
            WriteU16(bytes, offset + 12, namedCount);
            WriteU16(bytes, offset + 14, idCount);
        }

        private static void WriteDirectoryEntry(byte[] bytes, int offset, uint id, uint offsetToDataOrDirectory)
        {
            WriteU32(bytes, offset, id);
            WriteU32(bytes, offset + 4, offsetToDataOrDirectory);
        }

        private static void WriteDataEntry(byte[] bytes, int offset, int dataRva, int size)
        {
            WriteU32(bytes, offset, (uint)dataRva);
            WriteU32(bytes, offset + 4, (uint)size);
            WriteU32(bytes, offset + 8, 0);          // CodePage
            WriteU32(bytes, offset + 12, 0);         // Reserved
        }

        private static void WriteU32(byte[] bytes, int offset, uint value)
        {
            bytes[offset] = (byte)value;
            bytes[offset + 1] = (byte)(value >> 8);
            bytes[offset + 2] = (byte)(value >> 16);
            bytes[offset + 3] = (byte)(value >> 24);
        }

        private static void WriteU16(byte[] bytes, int offset, ushort value)
        {
            bytes[offset] = (byte)value;
            bytes[offset + 1] = (byte)(value >> 8);
        }

        private static uint ReadU32(byte[] bytes, int offset)
            => (uint)(bytes[offset] | (bytes[offset + 1] << 8) | (bytes[offset + 2] << 16) | (bytes[offset + 3] << 24));
    }
}
