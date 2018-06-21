// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Runtime.CompilerServices;

using ILCompiler.DependencyAnalysis;

namespace ILCompiler.PEWriter
{
    /// <summary>
    /// For a given symbol, this structure represents its target section and offset
    /// within the containing section.
    /// </summary>
    public struct SymbolTarget
    {
        /// <summary>
        /// Index of the section holding the symbol target.
        /// </summary>
        public readonly int SectionIndex;
        
        /// <summary>
        /// Offset of the symbol within the section.
        /// </summary>
        public readonly int Offset;
        
        /// <summary>
        /// Initialize symbol target with section and offset.
        /// </summary>
        /// <param name="sectionIndex">Section index where the symbol target resides</param>
        /// <param name="offset">Offset of the target within the section</param>
        public SymbolTarget(int sectionIndex, int offset)
        {
            SectionIndex = sectionIndex;
            Offset = offset;
        }
    }
    
    /// <summary>
    /// After placing an ObjectData within a section, we use this helper structure to record
    /// its relocation information for the final relocation pass.
    /// </summary>
    public struct ObjectDataRelocations
    {
        /// <summary>
        /// Offset of the ObjectData block within the section
        /// </summary>
        public readonly int Offset;

        /// <summary>
        /// List of relocations for the data block
        /// </summary>
        public readonly Relocation[] Relocs;
        
        /// <summary>
        /// Initialize the list of relocations for a given location within the section.
        /// </summary>
        /// <param name="offset">Offset within the section</param>
        /// <param name="relocs">List of relocations to apply at the offset</param>
        public ObjectDataRelocations(int offset, Relocation[] relocs)
        {
            Offset = offset;
            Relocs = relocs;
        }
    }
    
    /// <summary>
    /// Section represents a contiguous area of code or data with the same characteristics.
    /// </summary>
    public class Section
    {
        /// <summary>
        /// Index within the internal section table used by the section builder
        /// </summary>
        public readonly int Index;

        /// <summary>
        /// Section name
        /// </summary>
        public readonly string Name;

        /// <summary>
        /// Section characteristics
        /// </summary>
        public readonly SectionCharacteristics Characteristics;

        /// <summary>
        /// Alignment to apply when combining multiple builder sections into a single
        /// physical output section (typically when combining hot and cold code into
        /// the output code section).
        /// </summary>
        public readonly int Alignment;
        
        /// <summary>
        /// Blob builder representing the section content.
        /// </summary>
        public readonly BlobBuilder Content;

        /// <summary>
        /// Relocations to apply to the section
        /// </summary>
        public readonly List<ObjectDataRelocations> Relocations;

        /// <summary>
        /// RVA gets filled in during section serialization.
        /// </summary>
        public int RVAWhenPlaced;

        /// <summary>
        /// Output file position gets filled in during section serialization.
        /// </summary>
        public int FilePosWhenPlaced;

        /// <summary>
        /// Construct a new session object.
        /// </summary>
        /// <param name="index">Zero-based section index</param>
        /// <param name="name">Section name</param>
        /// <param name="characteristics">Section characteristics</param>
        /// <param name="alignment">Alignment for combining multiple logical sections</param>
        public Section(int index, string name, SectionCharacteristics characteristics, int alignment)
        {
            Index = index;
            Name = name;
            Characteristics = characteristics;
            Alignment = alignment;
            Content = new BlobBuilder();
            Relocations = new List<ObjectDataRelocations>();
            RVAWhenPlaced = 0;
            FilePosWhenPlaced = 0;
        }
    }

    /// <summary>
    /// This class represents a single export symbol in the PE file.
    /// </summary>
    public class ExportSymbol
    {
        /// <summary>
        /// Symbol identifier
        /// </summary>
        public readonly string Name;

        /// <summary>
        /// When placed into the export section, RVA of the symbol name gets updated.
        /// </summary>
        public int NameRVAWhenPlaced;

        /// <summary>
        /// Export symbol ordinal
        /// </summary>
        public readonly int Ordinal;

        /// <summary>
        /// Symbol to export
        /// </summary>
        public readonly ISymbolNode Symbol;

        /// <summary>
        /// Construct the export symbol instance filling in its arguments
        /// </summary>
        /// <param name="name">Export symbol identifier</param>
        /// <param name="ordinal">Ordinal ID of the export symbol</param>
        /// <param name="symbol">Symbol to export</param>
        public ExportSymbol(string name, int ordinal, ISymbolNode symbol)
        {
            Name = name;
            Ordinal = ordinal;
            Symbol = symbol;
        }
    }

    /// <summary>
    /// Section builder is capable of accumulating blocks, using them to lay out sections
    /// and relocate the produced executable according to the block relocation information.
    /// </summary>
    public class SectionBuilder
    {
        /// <summary>
        /// Map from symbols to their target sections and offsets.
        /// </summary>
        Dictionary<ISymbolNode, SymbolTarget> _symbolMap;

        /// <summary>
        /// List of sections defined in the builder
        /// </summary>
        List<Section> _sections;

        /// <summary>
        /// Symbols to export from the PE file.
        /// </summary>
        List<ExportSymbol> _exportSymbols;

        /// <summary>
        /// Optional symbol representing an entrypoint override.
        /// </summary>
        ISymbolNode _entryPointSymbol;

        /// <summary>
        /// Export directory entry when available.
        /// </summary>
        DirectoryEntry _exportDirectoryEntry;

        /// <summary>
        /// Directory entry representing the extra relocation records.
        /// </summary>
        DirectoryEntry _relocationDirectoryEntry;

        /// <summary>
        /// Symbol representing the ready-to-run header table.
        /// </summary>
        ISymbolNode _readyToRunHeaderSymbol;

        /// <summary>
        /// Size of the ready-to-run header table in bytes.
        /// </summary>
        int _readyToRunHeaderSize;

        /// <summary>
        /// For PE files with exports, this is the "DLL name" string to store in the export directory table.
        /// </summary>
        string _dllNameForExportDirectoryTable;

        /// <summary>
        /// Construct an empty section builder without any sections or blocks.
        /// </summary>
        public SectionBuilder()
        {
            _symbolMap = new Dictionary<ISymbolNode, SymbolTarget>();
            _sections = new List<Section>();
            _exportSymbols = new List<ExportSymbol>();
            _entryPointSymbol = null;
            _exportDirectoryEntry = default(DirectoryEntry);
            _relocationDirectoryEntry = default(DirectoryEntry);
        }

        /// <summary>
        /// Add a new section. Section names must be unique.
        /// </summary>
        /// <param name="name">Section name</param>
        /// <param name="characteristics">Section characteristics</param>
        /// <param name="alignment">
        /// Alignment for composing multiple builder sections into one physical output section
        /// </param>
        /// <returns>Zero-based index of the added section</returns>
        public int AddSection(string name, SectionCharacteristics characteristics, int alignment)
        {
            int sectionIndex = _sections.Count;
            _sections.Add(new Section(sectionIndex, name, characteristics, alignment));
            return sectionIndex;
        }

        /// <summary>
        /// Try to look up a pre-existing section in the builder; returns null if not found.
        /// </summary>
        public Section FindSection(string name)
        {
            return _sections.FirstOrDefault((sec) => sec.Name == name);
        }

        /// <summary>
        /// Attach an export symbol to the output PE file.
        /// </summary>
        /// <param name="name">Export symbol identifier</param>
        /// <param name="ordinal">Ordinal ID of the export symbol</param>
        /// <param name="symbol">Symbol to export</param>
        public void AddExportSymbol(string name, int ordinal, ISymbolNode symbol)
        {
            _exportSymbols.Add(new ExportSymbol(
                name: name,
                ordinal: ordinal,
                symbol: symbol));
        }

        /// <summary>
        /// Record DLL name to emit in the export directory table.
        /// </summary>
        /// <param name="dllName">DLL name to emit</param>
        public void SetDllNameForExportDirectoryTable(string dllName)
        {
            _dllNameForExportDirectoryTable = dllName;
        }

        /// <summary>
        /// Override entry point for the app.
        /// </summary>
        /// <param name="symbol">Symbol representing the new entry point</param>
        public void SetEntryPoint(ISymbolNode symbol)
        {
            _entryPointSymbol = symbol;
        }

        /// <summary>
        /// Set up the ready-to-run header table location.
        /// </summary>
        /// <param name="symbol">Symbol representing the ready-to-run header</param>
        /// <param name="headerSize">Size of the ready-to-run header</param>
        public void SetReadyToRunHeaderTable(ISymbolNode symbol, int headerSize)
        {
            _readyToRunHeaderSymbol = symbol;
            _readyToRunHeaderSize = headerSize;
        }

        /// <summary>
        /// Add an ObjectData block to a given section.
        /// </summary>
        /// <param name="data">Block to add</param>
        /// <param name="sectionIndex">Section index</param>
        public void AddObjectData(ObjectNode.ObjectData objectData, int sectionIndex)
        {
            Section section = _sections[sectionIndex];

            // Calculate alignment padding
            int alignedOffset = (section.Content.Count + objectData.Alignment - 1) & -objectData.Alignment;
            int padding = alignedOffset - section.Content.Count;

            if (padding > 0)
            {
                section.Content.WriteBytes(0, padding);
            }

            section.Content.WriteBytes(objectData.Data);

            if (objectData.DefinedSymbols != null)
            {
                foreach (ISymbolDefinitionNode symbol in objectData.DefinedSymbols)
                {
                    _symbolMap.Add(symbol, new SymbolTarget(
                        sectionIndex: sectionIndex,
                        offset: alignedOffset + symbol.Offset));
                }
            }

            if (objectData.Relocs != null)
            {
                section.Relocations.Add(new ObjectDataRelocations(alignedOffset, objectData.Relocs));
            }
        }

        /// <summary>
        /// Get the list of sections that need to be emitted to the output PE file.
        /// We filter out name duplicates as we'll end up merging builder sections with the same name
        /// into a single output physical section.
        /// </summary>
        public IEnumerable<(string SectionName, SectionCharacteristics Characteristics)> GetSections()
        {
            List<(string SectionName, SectionCharacteristics Characteristics)> sectionList =
                new List<(string SectionName, SectionCharacteristics Characteristics)>();
            foreach (Section section in _sections)
            {
                if (!sectionList.Any((sc) => sc.SectionName == section.Name))
                {
                    sectionList.Add((SectionName: section.Name, Characteristics: section.Characteristics));
                }
            }

            if (_exportSymbols.Count != 0 && FindSection(".edata") == null)
            {
                sectionList.Add((SectionName: ".edata", Characteristics:
                    SectionCharacteristics.ContainsInitializedData |
                    SectionCharacteristics.MemRead));
            }

            return sectionList;
        }

        /// <summary>
        /// Traverse blocks within a single section and use them to calculate final layout
        /// of the given section.
        /// </summary>
        /// <param name="name">Section to serialize</param>
        /// <param name="sectionLocation">Logical section address within the output PE file</param>
        /// <returns></returns>
        public BlobBuilder SerializeSection(string name, SectionLocation sectionLocation)
        {
            if (name == ".reloc")
            {
                return SerializeRelocationSection(sectionLocation);
            }
            
            if (name == ".edata")
            {
                return SerializeExportSection(sectionLocation);
            }

            BlobBuilder serializedSection = null;

            // Locate logical section index by name
            foreach (Section section in _sections.Where((sec) => sec.Name == name))
            {
                // Calculate alignment padding
                int alignedRVA = (sectionLocation.RelativeVirtualAddress + section.Alignment - 1) & -section.Alignment;
                int padding = alignedRVA - sectionLocation.RelativeVirtualAddress;
                if (padding > 0)
                {
                    if (serializedSection == null)
                    {
                        serializedSection = new BlobBuilder();
                    }
                    serializedSection.WriteBytes(0, padding);
                    sectionLocation = new SectionLocation(
                        sectionLocation.RelativeVirtualAddress + padding,
                        sectionLocation.PointerToRawData + padding);
                }

                // Place the section
                section.RVAWhenPlaced = sectionLocation.RelativeVirtualAddress;
                section.FilePosWhenPlaced = sectionLocation.PointerToRawData;

                if (section.Content.Count != 0)
                {
                    sectionLocation = new SectionLocation(
                        sectionLocation.RelativeVirtualAddress + section.Content.Count,
                        sectionLocation.PointerToRawData + section.Content.Count);

                    if (serializedSection == null)
                    {
                        serializedSection = section.Content;
                    }
                    else
                    {
                        serializedSection.LinkSuffix(section.Content);
                    }
                }
            }

            return serializedSection;
        }

        /// <summary>
        /// Emit the .reloc section based on file relocation information in the individual blocks.
        /// We rely on the fact that the .reloc section is emitted last so that, by the time
        /// it's getting serialized, all other sections that may contain relocations have already
        /// been laid out.
        /// </summary>
        private BlobBuilder SerializeRelocationSection(SectionLocation sectionLocation)
        {
            // There are 12 bits for the relative offset
            const int RelocationTypeShift = 12;
            const int MaxRelativeOffsetInBlock = (1 << RelocationTypeShift) - 1;

            // Even though the format doesn't dictate it, it seems customary
            // to align the base RVA's on 4K boundaries.
            const int BaseRVAAlignment = 1 << RelocationTypeShift;
            
            BlobBuilder builder = new BlobBuilder();
            int baseRVA = 0;
            List<ushort> offsetsAndTypes = null;

            // Traverse relocations in all sections in their RVA order
            // By now, all "normal" sections with relocations should already have been laid out
            foreach (Section section in _sections.OrderBy((sec) => sec.RVAWhenPlaced))
            {
                foreach (ObjectDataRelocations objectDataRelocs in section.Relocations)
                {
                    for (int relocIndex = 0; relocIndex < objectDataRelocs.Relocs.Length; relocIndex++)
                    {
                        RelocType relocType = objectDataRelocs.Relocs[relocIndex].RelocType;
                        RelocType fileRelocType = GetFileRelocationType(relocType);
                        if (fileRelocType != RelocType.IMAGE_REL_BASED_ABSOLUTE)
                        {
                            int relocationRVA = section.RVAWhenPlaced + objectDataRelocs.Offset + objectDataRelocs.Relocs[relocIndex].Offset;
                            if (offsetsAndTypes != null && relocationRVA - baseRVA > MaxRelativeOffsetInBlock)
                            {
                                // Need to flush relocation block as the current RVA is too far from base RVA
                                FlushRelocationBlock(builder, baseRVA, offsetsAndTypes);
                                offsetsAndTypes = null;
                            }
                            if (offsetsAndTypes == null)
                            {
                                // Create new relocation block
                                baseRVA = relocationRVA & -BaseRVAAlignment;
                                offsetsAndTypes = new List<ushort>();
                            }
                            ushort offsetAndType = (ushort)(((ushort)fileRelocType << RelocationTypeShift) | (relocationRVA - baseRVA));
                            offsetsAndTypes.Add(offsetAndType);
                        }
                    }
                }
            }

            if (offsetsAndTypes != null)
            {
                FlushRelocationBlock(builder, baseRVA, offsetsAndTypes);
            }

            _relocationDirectoryEntry = new DirectoryEntry(sectionLocation.RelativeVirtualAddress, builder.Count);

            return builder;
        }

        /// <summary>
        /// Serialize a block of relocations into the .reloc section.
        /// </summary>
        /// <param name="builder">Output blob builder to receive the serialized relocation block</param>
        /// <param name="baseRVA">Base RVA of the relocation block</param>
        /// <param name="offsetsAndTypes">16-bit entries encoding offset relative to the base RVA (low 12 bits) and relocation type (top 4 bite)</param>
        private static void FlushRelocationBlock(BlobBuilder builder, int baseRVA, List<ushort> offsetsAndTypes)
        {
            // First, emit the block header: 4 bytes starting RVA,
            builder.WriteInt32(baseRVA);
            // followed by the total block size comprising this header
            // and following 16-bit entries.
            builder.WriteInt32(4 + 4 + 2 * offsetsAndTypes.Count);
            // Now serialize out the entries
            foreach (ushort offsetAndType in offsetsAndTypes)
            {
                builder.WriteUInt16(offsetAndType);
            }
        }

        /// <summary>
        /// Serialize the export symbol table into the export section.
        /// </summary>
        /// <param name="location">RVA and file location of the .edata section</param>
        private BlobBuilder SerializeExportSection(SectionLocation sectionLocation)
        {
            _exportSymbols.Sort((es1, es2) => StringComparer.Ordinal.Compare(es1.Name, es2.Name));
            
            BlobBuilder builder = new BlobBuilder();

            int minOrdinal = int.MaxValue;
            int maxOrdinal = int.MinValue;

            // First, emit the name table and store the name RVA's for the individual export symbols
            // Also, record the ordinal range.
            foreach (ExportSymbol symbol in _exportSymbols)
            {
                symbol.NameRVAWhenPlaced = sectionLocation.RelativeVirtualAddress + builder.Count;
                builder.WriteUTF8(symbol.Name);
                builder.WriteByte(0);
                
                if (symbol.Ordinal < minOrdinal)
                {
                    minOrdinal = symbol.Ordinal;
                }
                if (symbol.Ordinal > maxOrdinal)
                {
                    maxOrdinal = symbol.Ordinal;
                }
            }

            // Emit the DLL name
            int dllNameRVA = sectionLocation.RelativeVirtualAddress + builder.Count;
            builder.WriteUTF8(_dllNameForExportDirectoryTable);
            builder.WriteByte(0);

            int[] addressTable = new int[maxOrdinal - minOrdinal + 1];

            // Emit the name pointer table; it should be alphabetically sorted.
            // Also, we can now fill in the export address table as we've detected its size
            // in the previous pass.
            int namePointerTableRVA = sectionLocation.RelativeVirtualAddress + builder.Count;
            foreach (ExportSymbol symbol in _exportSymbols)
            {
                builder.WriteInt32(symbol.NameRVAWhenPlaced);
                SymbolTarget symbolTarget = _symbolMap[symbol.Symbol];
                Section symbolSection = _sections[symbolTarget.SectionIndex];
                Debug.Assert(symbolSection.RVAWhenPlaced != 0);
                addressTable[symbol.Ordinal - minOrdinal] = symbolSection.RVAWhenPlaced + symbolTarget.Offset;
            }

            // Emit the ordinal table
            int ordinalTableRVA = sectionLocation.RelativeVirtualAddress + builder.Count;
            foreach (ExportSymbol symbol in _exportSymbols)
            {
                builder.WriteUInt16((ushort)(symbol.Ordinal - minOrdinal));
            }

            // Emit the address table
            int addressTableRVA = sectionLocation.RelativeVirtualAddress + builder.Count;
            foreach (int addressTableEntry in addressTable)
            {
                builder.WriteInt32(addressTableEntry);
            }
            
            // Emit the export directory table
            int exportDirectoryTableRVA = sectionLocation.RelativeVirtualAddress + builder.Count;
            // +0x00: reserved
            builder.WriteInt32(0);
            // +0x04: TODO: time/date stamp
            builder.WriteInt32(0);
            // +0x08: major version
            builder.WriteInt16(0);
            // +0x0A: minor version
            builder.WriteInt16(0);
            // +0x0C: DLL name RVA
            builder.WriteInt32(dllNameRVA);
            // +0x10: ordinal base
            builder.WriteInt32(minOrdinal);
            // +0x14: number of entries in the address table
            builder.WriteInt32(addressTable.Length);
            // +0x18: number of name pointers
            builder.WriteInt32(_exportSymbols.Count);
            // +0x1C: export address table RVA
            builder.WriteInt32(addressTableRVA);
            // +0x20: name pointer RVV
            builder.WriteInt32(namePointerTableRVA);
            // +0x24: ordinal table RVA
            builder.WriteInt32(ordinalTableRVA);
            int exportDirectorySize = sectionLocation.RelativeVirtualAddress + builder.Count - exportDirectoryTableRVA;

            _exportDirectoryEntry = new DirectoryEntry(relativeVirtualAddress: exportDirectoryTableRVA, size: exportDirectorySize);
            
            return builder;
        }

        /// <summary>
        /// Update the PE file directories. Currently this is used to update the export symbol table
        /// when export symbols have been added to the section builder.
        /// </summary>
        /// <param name="directoriesBuilder">PE directory builder to update</param>
        public void UpdateDirectories(PEDirectoriesBuilder directoriesBuilder)
        {
            if (_exportDirectoryEntry.Size != 0)
            {
                directoriesBuilder.ExportTable = _exportDirectoryEntry;
            }
            
            int relocationTableRVA = directoriesBuilder.BaseRelocationTable.RelativeVirtualAddress;
            if (relocationTableRVA == 0)
            {
                relocationTableRVA = _relocationDirectoryEntry.RelativeVirtualAddress;
            }
            directoriesBuilder.BaseRelocationTable = new DirectoryEntry(
                relocationTableRVA,
                directoriesBuilder.BaseRelocationTable.Size + _relocationDirectoryEntry.Size);

            if (_entryPointSymbol != null)
            {
                SymbolTarget symbolTarget = _symbolMap[_entryPointSymbol];
                Section section = _sections[symbolTarget.SectionIndex];
                Debug.Assert(section.RVAWhenPlaced != 0);
                directoriesBuilder.AddressOfEntryPoint = section.RVAWhenPlaced + symbolTarget.Offset;
            }
        }

        /// <summary>
        /// Update the COR header.
        /// </summary>
        /// <param name="corHeader">COR header builder to update</param>
        public void UpdateCorHeader(CorHeaderBuilder corHeader)
        {
            if (_readyToRunHeaderSymbol != null)
            {
                SymbolTarget headerTarget = _symbolMap[_readyToRunHeaderSymbol];
                Section headerSection = _sections[headerTarget.SectionIndex];
                Debug.Assert(headerSection.RVAWhenPlaced != 0);
                int r2rHeaderRVA = headerSection.RVAWhenPlaced + headerTarget.Offset;
                corHeader.ManagedNativeHeaderDirectory = new DirectoryEntry(r2rHeaderRVA, _readyToRunHeaderSize);
            }
        }

        /// <summary>
        /// Relocate the produced PE file and output the result into a given stream.
        /// </summary>
        /// <param name="peFile">Blob builder representing the complete PE file</param>
        /// <param name="defaultImageBase">Default load address for the image</param>
        /// <param name="corHeaderBuilder">COR header</param>
        /// <param name="corHeaderFileOffset">File position of the COR header</param>
        /// <param name="outputStream">Stream to receive the relocated PE file</param>
        public void RelocateOutputFile(
            BlobBuilder peFile,
            ulong defaultImageBase,
            CorHeaderBuilder corHeaderBuilder,
            int corHeaderFileOffset,
            Stream outputStream)
        {
            RelocationHelper relocationHelper = new RelocationHelper(outputStream, defaultImageBase, peFile);

            if (corHeaderBuilder != null)
            {
                relocationHelper.CopyToFilePosition(corHeaderFileOffset);
                UpdateCorHeader(corHeaderBuilder);
                BlobBuilder corHeaderBlob = new BlobBuilder();
                corHeaderBuilder.WriteTo(corHeaderBlob);
                int writtenSize = corHeaderBlob.Count;
                corHeaderBlob.WriteContentTo(outputStream);
                relocationHelper.AdvanceOutputPos(writtenSize);

                // Just skip the bytes that were emitted by the COR header writer
                byte[] skipBuffer = new byte[writtenSize];
                relocationHelper.CopyBytesToBuffer(skipBuffer, writtenSize);
            }

            // Traverse relocations in all sections in their RVA order
            foreach (Section section in _sections.OrderBy((sec) => sec.RVAWhenPlaced))
            {
                int rvaToFilePosDelta = section.FilePosWhenPlaced - section.RVAWhenPlaced;
                foreach (ObjectDataRelocations objectDataRelocs in section.Relocations)
                {
                    foreach (Relocation relocation in objectDataRelocs.Relocs)
                    {
                        // Process a single relocation
                        int relocationRVA = section.RVAWhenPlaced + objectDataRelocs.Offset + relocation.Offset;
                        int relocationFilePos = relocationRVA + rvaToFilePosDelta;

                        // Flush parts of PE file before the relocation to the output stream
                        relocationHelper.CopyToFilePosition(relocationFilePos);

                        // Look up relocation target
                        SymbolTarget relocationTarget = _symbolMap[relocation.Target];
                        Section targetSection = _sections[relocationTarget.SectionIndex];
                        int targetRVA = targetSection.RVAWhenPlaced + relocationTarget.Offset;

                        // Apply the relocation
                        relocationHelper.ProcessRelocation(relocation.RelocType, relocationRVA, targetRVA);
                    }
                }
            }

            // Flush remaining PE file blocks after the last relocation
            relocationHelper.CopyRestOfFile();
        }

        /// <summary>
        /// Return file relocation type for the given relocation type. If the relocation
        /// doesn't require a file-level relocation entry in the .reloc section, 0 is returned
        /// corresponding to the IMAGE_REL_BASED_ABSOLUTE no-op relocation record.
        /// </summary>
        /// <param name="relocationType">Relocation type</param>
        /// <returns>File-level relocation type or 0 (IMAGE_REL_BASED_ABSOLUTE) if none is required</returns>
        private static RelocType GetFileRelocationType(RelocType relocationType)
        {
            switch (relocationType)
            {
                case RelocType.IMAGE_REL_BASED_HIGHLOW:
                case RelocType.IMAGE_REL_BASED_DIR64:
                case RelocType.IMAGE_REL_BASED_THUMB_MOV32:
                    return relocationType;
                    
                default:
                    return RelocType.IMAGE_REL_BASED_ABSOLUTE;
            }
        }
    }
    
    /// <summary>
    /// This class has been mostly copied over from corefx as the corefx CorHeader class
    /// is well protected against being useful in more general scenarios as its only
    /// constructor is internal.
    /// </summary>
    public sealed class CorHeaderBuilder
    {
        public int CorHeaderSize;
        public ushort MajorRuntimeVersion;
        public ushort MinorRuntimeVersion;
        public DirectoryEntry MetadataDirectory;
        public CorFlags Flags;
        public int EntryPointTokenOrRelativeVirtualAddress;
        public DirectoryEntry ResourcesDirectory;
        public DirectoryEntry StrongNameSignatureDirectory;
        public DirectoryEntry CodeManagerTableDirectory;
        public DirectoryEntry VtableFixupsDirectory;
        public DirectoryEntry ExportAddressTableJumpsDirectory;
        public DirectoryEntry ManagedNativeHeaderDirectory;

        public CorHeaderBuilder(ref BlobReader reader)
        {
            // byte count
            CorHeaderSize = reader.ReadInt32();

            MajorRuntimeVersion = reader.ReadUInt16();
            MinorRuntimeVersion = reader.ReadUInt16();
            MetadataDirectory = ReadDirectoryEntry(ref reader);
            Flags = (CorFlags)reader.ReadUInt32();
            EntryPointTokenOrRelativeVirtualAddress = reader.ReadInt32();
            ResourcesDirectory = ReadDirectoryEntry(ref reader);
            StrongNameSignatureDirectory = ReadDirectoryEntry(ref reader);
            CodeManagerTableDirectory = ReadDirectoryEntry(ref reader);
            VtableFixupsDirectory = ReadDirectoryEntry(ref reader);
            ExportAddressTableJumpsDirectory = ReadDirectoryEntry(ref reader);
            ManagedNativeHeaderDirectory = ReadDirectoryEntry(ref reader);
        }
        
        /// <summary>
        /// Helper method to serialize CorHeader into a BlobBuilder.
        /// </summary>
        /// <param name="builder">Target blob builder to receive the serialized data</param>
        public void WriteTo(BlobBuilder builder)
        {
            builder.WriteInt32(CorHeaderSize);

            builder.WriteUInt16(MajorRuntimeVersion);
            builder.WriteUInt16(MinorRuntimeVersion);

            WriteDirectoryEntry(MetadataDirectory, builder);

            builder.WriteUInt32((uint)Flags);
            builder.WriteInt32(EntryPointTokenOrRelativeVirtualAddress);

            WriteDirectoryEntry(ResourcesDirectory, builder);
            WriteDirectoryEntry(StrongNameSignatureDirectory, builder);
            WriteDirectoryEntry(CodeManagerTableDirectory, builder);
            WriteDirectoryEntry(VtableFixupsDirectory, builder);
            WriteDirectoryEntry(ExportAddressTableJumpsDirectory, builder);
            WriteDirectoryEntry(ManagedNativeHeaderDirectory, builder);
        }

        /// <summary>
        /// Deserialize a directory entry from a blob reader.
        /// </summary>
        /// <param name="reader">Reader to deserialize directory entry from</param>
        private static DirectoryEntry ReadDirectoryEntry(ref BlobReader reader)
        {
            int rva = reader.ReadInt32();
            int size = reader.ReadInt32();
            return new DirectoryEntry(rva, size);
        }

        /// <summary>
        /// Serialize a directory entry into an output blob builder.
        /// </summary>
        /// <param name="directoryEntry">Directory entry to serialize</param>
        /// <param name="builder">Output blob builder to receive the serialized entry</param>
        private static void WriteDirectoryEntry(DirectoryEntry directoryEntry, BlobBuilder builder)
        {
            builder.WriteInt32(directoryEntry.RelativeVirtualAddress);
            builder.WriteInt32(directoryEntry.Size);
        }
    }
    
    /// <summary>
    /// Section builder extensions for R2R PE builder.
    /// </summary>
    public static class SectionBuilderExtensions
    {
        /// <summary>
        /// Emit built sections using the R2R PE writer.
        /// </summary>
        /// <param name="builder">Section builder to emit</param>
        /// <param name="inputReader">Input MSIL reader</param>
        /// <param name="outputStream">Output stream for the final R2R PE file</param>
        public static void EmitR2R(this SectionBuilder builder, PEReader inputReader, Stream outputStream)
        {
            R2RPEBuilder r2rBuilder = new R2RPEBuilder(
                peReader: inputReader,
                sectionNames: builder.GetSections(),
                sectionSerializer: builder.SerializeSection,
                directoriesUpdater: builder.UpdateDirectories);

            BlobBuilder outputPeFile = new BlobBuilder();
            r2rBuilder.Serialize(outputPeFile);

            CorHeaderBuilder corHeader = r2rBuilder.CorHeader;
            if (corHeader != null)
            {
                corHeader.Flags = (r2rBuilder.CorHeader.Flags & ~CorFlags.ILOnly) | CorFlags.ILLibrary;

                corHeader.MetadataDirectory = r2rBuilder.RelocateDirectoryEntry(corHeader.MetadataDirectory);
                corHeader.ResourcesDirectory = r2rBuilder.RelocateDirectoryEntry(corHeader.ResourcesDirectory);
                corHeader.StrongNameSignatureDirectory = r2rBuilder.RelocateDirectoryEntry(corHeader.StrongNameSignatureDirectory);
                corHeader.CodeManagerTableDirectory = r2rBuilder.RelocateDirectoryEntry(corHeader.CodeManagerTableDirectory);
                corHeader.VtableFixupsDirectory = r2rBuilder.RelocateDirectoryEntry(corHeader.VtableFixupsDirectory);
                corHeader.ExportAddressTableJumpsDirectory = r2rBuilder.RelocateDirectoryEntry(corHeader.ExportAddressTableJumpsDirectory);
                corHeader.ManagedNativeHeaderDirectory = r2rBuilder.RelocateDirectoryEntry(corHeader.ManagedNativeHeaderDirectory);

                builder.UpdateCorHeader(corHeader);
            }

            builder.RelocateOutputFile(
                outputPeFile,
                inputReader.PEHeaders.PEHeader.ImageBase,
                corHeader,
                r2rBuilder.CorHeaderFileOffset,
                outputStream);
        }
    }
}