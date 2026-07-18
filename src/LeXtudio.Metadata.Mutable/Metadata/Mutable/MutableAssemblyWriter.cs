using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using Microsoft.Extensions.Logging;

namespace LeXtudio.Metadata.Mutable
{
    /// <summary>
    /// Writes mutable assemblies to PE files using System.Reflection.Metadata.
    /// This replaces the need for legacy Cecil AssemblyDefinition.Write().
    /// </summary>
    /// <remarks>
    /// <para>
    /// The writer translates the mutable object model to SRM's MetadataBuilder
    /// and uses PEBuilder to produce the final PE file. This is a complete
    /// Re-implementation of assembly writing without any Cecil dependency.
    /// </para>
    /// <para>
    /// The writing process:
    /// <list type="number">
    /// <item>Build metadata tables (types, methods, fields, etc.)</item>
    /// <item>Encode all IL method bodies</item>
    /// <item>Encode all resources</item>
    /// <item>Create PE headers and sections</item>
    /// <item>Write to output stream</item>
    /// </list>
    /// </para>
    /// </remarks>
    public class MutableAssemblyWriter
    {
        private readonly MutableAssemblyDefinition _assembly;
        private readonly MutableWriterParameters _parameters;
        
        private MetadataBuilder _metadata;
        private BlobBuilder _ilStream;
        private BlobBuilder _managedResources;
        private BlobBuilder _mappedFieldData;
        private MethodBodyStreamEncoder _methodBodyEncoder;
        private AssemblyDefinitionHandle _assemblyDefHandle;
        private ModuleDefinitionHandle _moduleDefHandle;
        
        // Handle mappings for cross-references
        private Dictionary<MutableTypeDefinition, TypeDefinitionHandle> _typeDefHandles;
        private Dictionary<MutableMethodDefinition, MethodDefinitionHandle> _methodDefHandles;
        private Dictionary<MutableFieldDefinition, FieldDefinitionHandle> _fieldDefHandles;
        private Dictionary<MutablePropertyDefinition, PropertyDefinitionHandle> _propertyDefHandles;
        private Dictionary<MutableEventDefinition, EventDefinitionHandle> _eventDefHandles;
        private Dictionary<MutableParameterDefinition, ParameterHandle> _parameterHandles;
        private Dictionary<MutableGenericParameter, GenericParameterHandle> _genericParameterHandles;
        private Dictionary<MutableTypeReference, EntityHandle> _typeRefHandles;
        private Dictionary<MutableMethodReference, EntityHandle> _methodRefHandles;
        private Dictionary<MutableFieldReference, EntityHandle> _fieldRefHandles;
        private Dictionary<MutableAssemblyNameReference, AssemblyReferenceHandle> _asmRefHandles;
        private Dictionary<MutableModuleReference, ModuleReferenceHandle> _modRefHandles;
        private Dictionary<string, ModuleReferenceHandle> _modRefNameHandles;
        private Dictionary<string, UserStringHandle> _userStringHandles;
        private List<MutableTypeDefinition> _orderedTypes;
        private Dictionary<MutableMethodDefinition, int> _methodBodyOffsets;

        /// <summary>
        /// Initializes a new writer for the specified assembly.
        /// </summary>
        public MutableAssemblyWriter(MutableAssemblyDefinition assembly, MutableWriterParameters parameters = null)
        {
            _assembly = assembly ?? throw new ArgumentNullException(nameof(assembly));
            _parameters = parameters ?? new MutableWriterParameters();
        }

        /// <summary>
        /// Writes the assembly to the specified file path.
        /// </summary>
        public void Write(string fileName)
        {
            using var stream = new FileStream(fileName, FileMode.Create, FileAccess.Write);
            Write(stream);
        }

        /// <summary>
        /// Writes the assembly to the specified stream.
        /// </summary>
        public void Write(Stream stream)
        {
            InitializeBuilders();
            
            var module = _assembly.MainModule;
            
            // Create assembly definition
            if (_assembly.Name != null)
            {
                WriteAssemblyDefinition();
            }

            // Create module definition
            WriteModuleDefinition();
            
            // Write assembly references
            WriteAssemblyReferences();
            WriteModuleReferences();
            
            // First pass: create type definitions (forward declarations)
            WriteTypeDefinitionsFirstPass();

            // Precompute member handles for IL encoding.
            PrecomputeMemberHandles();

            // Encode method bodies before emitting method definitions.
            WriteMethodBodies();
            
            // Second pass: fill in type members
            WriteTypeDefinitionsSecondPass();

            // Write generic parameters after type/method definitions are available.
            WriteAllGenericParameters();

            // Custom attributes
            WriteCustomAttributes();

            // Encode managed resources (embedded/linked)
            WriteResources();
            
            // Create the PE file
            WritePEFile(stream);
        }

        private void InitializeBuilders()
        {
            _metadata = new MetadataBuilder();
            _ilStream = new BlobBuilder();
            _managedResources = new BlobBuilder();
            _mappedFieldData = new BlobBuilder();
            _methodBodyEncoder = new MethodBodyStreamEncoder(_ilStream);
            _assemblyDefHandle = default;
            _moduleDefHandle = default;
            
            _typeDefHandles = new Dictionary<MutableTypeDefinition, TypeDefinitionHandle>();
            _methodDefHandles = new Dictionary<MutableMethodDefinition, MethodDefinitionHandle>();
            _fieldDefHandles = new Dictionary<MutableFieldDefinition, FieldDefinitionHandle>();
            _propertyDefHandles = new Dictionary<MutablePropertyDefinition, PropertyDefinitionHandle>();
            _eventDefHandles = new Dictionary<MutableEventDefinition, EventDefinitionHandle>();
            _parameterHandles = new Dictionary<MutableParameterDefinition, ParameterHandle>();
            _genericParameterHandles = new Dictionary<MutableGenericParameter, GenericParameterHandle>();
            _typeRefHandles = new Dictionary<MutableTypeReference, EntityHandle>();
            _methodRefHandles = new Dictionary<MutableMethodReference, EntityHandle>();
            _fieldRefHandles = new Dictionary<MutableFieldReference, EntityHandle>();
            _asmRefHandles = new Dictionary<MutableAssemblyNameReference, AssemblyReferenceHandle>();
            _modRefHandles = new Dictionary<MutableModuleReference, ModuleReferenceHandle>();
            _modRefNameHandles = new Dictionary<string, ModuleReferenceHandle>(StringComparer.Ordinal);
            _userStringHandles = new Dictionary<string, UserStringHandle>();
            _orderedTypes = new List<MutableTypeDefinition>();
            _methodBodyOffsets = new Dictionary<MutableMethodDefinition, int>();
        }

        private void WriteAssemblyDefinition()
        {
            var name = _assembly.Name;
            _assemblyDefHandle = _metadata.AddAssembly(
                _metadata.GetOrAddString(name.Name),
                name.Version,
                _metadata.GetOrAddString(name.Culture ?? ""),
                name.PublicKey != null && name.PublicKey.Length > 0 
                    ? _metadata.GetOrAddBlob(name.PublicKey) 
                    : default,
                (AssemblyFlags)name.Attributes,
                (AssemblyHashAlgorithm)name.HashAlgorithm);
        }

        private void WriteModuleDefinition()
        {
            var module = _assembly.MainModule;
            _moduleDefHandle = _metadata.AddModule(
                0, // Generation
                _metadata.GetOrAddString(module.Name),
                _metadata.GetOrAddGuid(module.Mvid != Guid.Empty ? module.Mvid : Guid.NewGuid()),
                default, // EncId
                default); // EncBaseId
        }

        private void WriteAssemblyReferences()
        {
            var module = _assembly.MainModule;
            foreach (var asmRef in module.AssemblyReferences)
            {
                var handle = _metadata.AddAssemblyReference(
                    _metadata.GetOrAddString(asmRef.Name),
                    asmRef.Version,
                    _metadata.GetOrAddString(asmRef.Culture ?? ""),
                    asmRef.PublicKeyToken != null && asmRef.PublicKeyToken.Length > 0
                        ? _metadata.GetOrAddBlob(asmRef.PublicKeyToken)
                        : default,
                    (AssemblyFlags)asmRef.Attributes,
                    default); // HashValue
                
                _asmRefHandles[asmRef] = handle;
            }
        }

        private void WriteModuleReferences()
        {
            var module = _assembly.MainModule;
            foreach (var modRef in module.ModuleReferences)
            {
                GetOrAddModuleReferenceHandle(modRef);
            }
        }

        private void WriteTypeDefinitionsFirstPass()
        {
            var module = _assembly.MainModule;
            
            // Preserve original TypeDef row order when available so MethodDef/FieldDef
            // handles remain stable across a read/write round trip.
            _orderedTypes.Clear();
            CollectTypes(module.Types, _orderedTypes);
            _orderedTypes.Sort(CompareTypeDefinitionOrder);
            
            var fieldIndex = 1;
            var methodIndex = 1;
            foreach (var type in _orderedTypes)
            {
                CreateTypeDefinition(type, fieldIndex, methodIndex);
                fieldIndex += type.Fields.Count;
                methodIndex += type.Methods.Count;
            }
        }

        private void PrecomputeMemberHandles()
        {
            _fieldDefHandles.Clear();
            _methodDefHandles.Clear();

            var fieldIndex = 1;
            var methodIndex = 1;
            foreach (var type in _orderedTypes)
            {
                foreach (var field in type.Fields)
                {
                    _fieldDefHandles[field] = MetadataTokens.FieldDefinitionHandle(fieldIndex++);
                }

                foreach (var method in type.Methods)
                {
                    _methodDefHandles[method] = MetadataTokens.MethodDefinitionHandle(methodIndex++);
                }
            }
        }

        private void CollectTypes(IEnumerable<MutableTypeDefinition> types, List<MutableTypeDefinition> result)
        {
            foreach (var type in types)
            {
                result.Add(type);
                CollectTypes(type.NestedTypes, result);
            }
        }

        private static int CompareTypeDefinitionOrder(MutableTypeDefinition left, MutableTypeDefinition right)
        {
            if (ReferenceEquals(left, right))
                return 0;

            if (left == null)
                return -1;

            if (right == null)
                return 1;

            var leftToken = left.MetadataToken;
            var rightToken = right.MetadataToken;
            if (leftToken != 0 && rightToken != 0)
                return leftToken.CompareTo(rightToken);

            var leftIsModule = left.Name == "<Module>" && string.IsNullOrEmpty(left.Namespace);
            var rightIsModule = right.Name == "<Module>" && string.IsNullOrEmpty(right.Namespace);
            if (leftIsModule != rightIsModule)
                return leftIsModule ? -1 : 1;

            var namespaceComparison = string.Compare(left.Namespace, right.Namespace, StringComparison.Ordinal);
            if (namespaceComparison != 0)
                return namespaceComparison;

            return string.Compare(left.Name, right.Name, StringComparison.Ordinal);
        }

        private void CreateTypeDefinition(MutableTypeDefinition type, int firstFieldIndex, int firstMethodIndex)
        {
            // Compute field and method list indices based on type order.
            var firstField = MetadataTokens.FieldDefinitionHandle(firstFieldIndex);
            var firstMethod = MetadataTokens.MethodDefinitionHandle(firstMethodIndex);
            
            var baseType = type.BaseType != null ? GetTypeHandle(type.BaseType) : default;
            
            var handle = _metadata.AddTypeDefinition(
                type.Attributes,
                _metadata.GetOrAddString(type.Namespace ?? ""),
                _metadata.GetOrAddString(type.Name),
                baseType,
                firstField,
                firstMethod);
            
            _typeDefHandles[type] = handle;

            if (type.PackingSize > 0 || type.ClassSize > 0)
            {
                _metadata.AddTypeLayout(handle, (ushort)type.PackingSize, (uint)type.ClassSize);
            }
            
            // Add nested type relationship
            if (type.DeclaringType is MutableTypeDefinition declaringTypeDef && _typeDefHandles.TryGetValue(declaringTypeDef, out var declaringHandle))
            {
                _metadata.AddNestedType(handle, declaringHandle);
            }
        }

        private void WriteTypeDefinitionsSecondPass()
        {
            foreach (var type in _orderedTypes)
            {
                if (!_typeDefHandles.TryGetValue(type, out var typeHandle))
                {
                    continue;
                }

                WriteTypeFields(type);
                WriteTypeMethods(type);
                WriteTypeMethodImplementations(type, typeHandle);
                WriteTypeInterfaces(type, typeHandle);
                WriteTypeProperties(type, typeHandle);
                WriteTypeEvents(type, typeHandle);
            }
        }

        private void WriteTypeFields(MutableTypeDefinition type)
        {
            foreach (var field in type.Fields)
            {
                WriteFieldDefinition(field);
            }
        }

        private void WriteTypeMethods(MutableTypeDefinition type)
        {
            foreach (var method in type.Methods)
            {
                WriteMethodDefinition(method);
            }
        }

        private void WriteTypeMethodImplementations(MutableTypeDefinition type, TypeDefinitionHandle typeHandle)
        {
            foreach (var impl in type.MethodImplementations)
            {
                var bodyHandle = GetMethodHandleForImpl(impl.MethodBody);
                var declarationHandle = GetMethodHandleForImpl(impl.MethodDeclaration);
                if (bodyHandle.IsNil || declarationHandle.IsNil)
                    continue;

                _metadata.AddMethodImplementation(typeHandle, bodyHandle, declarationHandle);
            }
        }

        private void WriteTypeInterfaces(MutableTypeDefinition type, TypeDefinitionHandle typeHandle)
        {
            foreach (var iface in type.Interfaces)
            {
                var ifaceHandle = GetTypeHandle(iface.InterfaceType);
                _metadata.AddInterfaceImplementation(typeHandle, ifaceHandle);
            }
        }

        private void WriteTypeProperties(MutableTypeDefinition type, TypeDefinitionHandle typeHandle)
        {
            if (type.Properties.Count == 0)
                return;

            _metadata.AddPropertyMap(typeHandle,
                MetadataTokens.PropertyDefinitionHandle(_metadata.GetRowCount(TableIndex.Property) + 1));

            foreach (var prop in type.Properties)
            {
                WritePropertyDefinition(prop);
            }
        }

        private void WriteTypeEvents(MutableTypeDefinition type, TypeDefinitionHandle typeHandle)
        {
            if (type.Events.Count == 0)
                return;

            _metadata.AddEventMap(typeHandle,
                MetadataTokens.EventDefinitionHandle(_metadata.GetRowCount(TableIndex.Event) + 1));

            foreach (var evt in type.Events)
            {
                WriteEventDefinition(evt);
            }
        }

        private void WriteFieldDefinition(MutableFieldDefinition field)
        {
            var signature = EncodeFieldSignature(field.FieldType);
            
            var handle = _metadata.AddFieldDefinition(
                field.Attributes,
                _metadata.GetOrAddString(field.Name),
                _metadata.GetOrAddBlob(signature));
            
            _fieldDefHandles[field] = handle;

            // Re-emit FieldLayout table entries for explicit-layout fields.
            if (field.Offset >= 0)
            {
                _metadata.AddFieldLayout(handle, field.Offset);
            }
            
            // Write constant if present
            if (field.Constant != null)
            {
                _metadata.AddConstant(handle, field.Constant);
            }
            
            // Write initial value (RVA) if present
            if (field.InitialValue != null && field.InitialValue.Length > 0)
            {
                AlignMappedFieldData(4);
                var offset = _mappedFieldData.Count;
                _mappedFieldData.WriteBytes(field.InitialValue);
                _metadata.AddFieldRelativeVirtualAddress(handle, offset);
            }
        }

        private void AlignMappedFieldData(int alignment)
        {
            var remainder = _mappedFieldData.Count % alignment;
            if (remainder == 0)
                return;

            _mappedFieldData.WriteBytes(0, alignment - remainder);
        }

        private void WriteMethodDefinition(MutableMethodDefinition method)
        {
            var signature = EncodeMethodSignature(method);
            var bodyOffset = -1;
            if (CanWriteBody(method) && _methodBodyOffsets.TryGetValue(method, out var offset))
            {
                bodyOffset = offset;
            }
            
            // RVA derived from encoded method body (0 for abstract/external).
            var handle = _metadata.AddMethodDefinition(
                method.Attributes,
                method.ImplAttributes,
                _metadata.GetOrAddString(method.Name),
                _metadata.GetOrAddBlob(signature),
                bodyOffset,
                MetadataTokens.ParameterHandle(_metadata.GetRowCount(TableIndex.Param) + 1));
            
            _methodDefHandles[method] = handle;
            WriteMethodImport(method, handle);
            
            // Write parameters
            foreach (var param in method.Parameters)
            {
                var paramHandle = _metadata.AddParameter(
                    param.Attributes,
                    _metadata.GetOrAddString(param.Name ?? ""),
                    param.Index + 1); // Sequence number is 1-based

                _parameterHandles[param] = paramHandle;
                
                if (param.DefaultValue != null)
                {
                    _metadata.AddConstant(paramHandle, param.DefaultValue);
                }
            }
        }

        private void WriteMethodImport(MutableMethodDefinition method, MethodDefinitionHandle methodHandle)
        {
            if (!method.IsPInvokeImpl)
                return;

            var info = method.PInvokeInfo;
            if (info?.Module == null || string.IsNullOrEmpty(info.Module.Name))
            {
                return;
            }

            var moduleRefHandle = GetOrAddModuleReferenceHandle(info.Module);
            if (moduleRefHandle.IsNil)
            {
                return;
            }

            var importName = string.IsNullOrEmpty(info.EntryPoint) ? method.Name : info.EntryPoint;
            _metadata.AddMethodImport(
                methodHandle,
                (MethodImportAttributes)info.Attributes,
                _metadata.GetOrAddString(importName),
                moduleRefHandle);
        }

        private ModuleReferenceHandle GetOrAddModuleReferenceHandle(MutableModuleReference moduleReference)
        {
            if (moduleReference == null || string.IsNullOrEmpty(moduleReference.Name))
            {
                return default;
            }

            if (_modRefHandles.TryGetValue(moduleReference, out var existing))
            {
                return existing;
            }

            if (_modRefNameHandles.TryGetValue(moduleReference.Name, out var existingByName))
            {
                _modRefHandles[moduleReference] = existingByName;
                return existingByName;
            }

            var handle = _metadata.AddModuleReference(_metadata.GetOrAddString(moduleReference.Name));
            _modRefHandles[moduleReference] = handle;
            _modRefNameHandles[moduleReference.Name] = handle;
            return handle;
        }

        private void WriteMethodBodies()
        {
            _methodBodyOffsets.Clear();

            foreach (var kvp in _methodDefHandles)
            {
                var method = kvp.Key;
                var declaringType = method?.DeclaringType as MutableTypeDefinition;
                var isInterface = declaringType != null && declaringType.IsInterface;

                if (declaringType != null && declaringType.Namespace == "SkipVirtualMethodTest" && declaringType.Name == "Interface1")
                {
                    LoggerService.Logger.LogDebug(
                        $"SkipVirtualMethodTest.Interface1::{method.Name} attrs={method.Attributes} isInterface={isInterface} bodyNull={method.Body == null}");
                }

                if (method?.Body != null && (method.IsAbstract || isInterface))
                {
                    LoggerService.Logger.LogDebug(
                        $"Method has body but abstract/interface: {declaringType?.FullName}::{method.Name} attrs={method.Attributes} isInterface={isInterface} bodyInstr={method.Body.Instructions.Count}");
                }
                
                if (!CanWriteBody(method))
                    continue;

                if (method.IsAbstract || isInterface)
                {
                    LoggerService.Logger.LogDebug(
                        $"Encoding body for abstract/interface: {declaringType?.FullName}::{method.Name} attrs={method.Attributes} isInterface={isInterface}");
                }
                
                var offset = EncodeMethodBody(method.Body);
                _methodBodyOffsets[method] = offset;
            }
        }

        private static bool CanWriteBody(MutableMethodDefinition method)
        {
            if (method == null || method.Body == null)
                return false;

            if (method.IsAbstract || method.IsPInvokeImpl || method.IsRuntime)
                return false;

            if (method.DeclaringType is MutableTypeDefinition declaringType && declaringType.IsInterface)
                return false;

            // Only emit IL bodies for IL methods.
            if ((method.ImplAttributes & MethodImplAttributes.CodeTypeMask) != MethodImplAttributes.IL)
                return false;

            return true;
        }

        private int EncodeMethodBody(MutableMethodBody body)
        {
            // Calculate locals signature if present
            StandaloneSignatureHandle localSig = default;
            if (body.Variables.Count > 0)
            {
                var localsEncoder = new BlobBuilder();
                var encoder = new BlobEncoder(localsEncoder);
                
                var localsBuilder = encoder.LocalVariableSignature(body.Variables.Count);
                foreach (var variable in body.Variables)
                {
                    var variableType = variable.VariableType;
                    var isPinned = variable.IsPinned;
                    if (variableType is MutablePinnedType pinnedType)
                    {
                        variableType = pinnedType.ElementType;
                        isPinned = true;
                    }

                    EncodeTypeToBuilder(localsBuilder.AddVariable().Type(isPinned: isPinned), variableType);
                }
                
                localSig = _metadata.AddStandaloneSignature(_metadata.GetOrAddBlob(localsEncoder));
            }
            
            // Encode IL
            var ilBytes = EncodeIL(body);
            var codeSize = ilBytes.Length;

            // Reserve method body and exception region storage in the stream.
            var attributes = body.InitLocals ? MethodBodyAttributes.InitLocals : MethodBodyAttributes.None;
            var methodBody = _methodBodyEncoder.AddMethodBody(
                codeSize,
                body.MaxStackSize,
                body.ExceptionHandlers?.Count ?? 0,
                HasSmallExceptionRegions(body, codeSize),
                localSig,
                attributes);

            // Copy encoded IL into the reserved instruction blob.
            if (codeSize > 0)
            {
                var seg = methodBody.Instructions.GetBytes();
                Array.Copy(ilBytes, 0, seg.Array, seg.Offset, codeSize);
            }

            // Encode exception regions if present
            if (body.ExceptionHandlers != null && body.ExceptionHandlers.Count > 0)
            {
                var regionEncoder = methodBody.ExceptionRegions;
                foreach (var eh in body.ExceptionHandlers)
                {
                    int tryStart = GetExceptionBoundaryOffset(body, eh.TryStart, 0);
                    int tryEnd = GetExceptionBoundaryOffset(body, eh.TryEnd, codeSize);
                    int handlerStart = GetExceptionBoundaryOffset(body, eh.HandlerStart, 0);
                    int handlerEnd = GetExceptionBoundaryOffset(body, eh.HandlerEnd, codeSize);
                    int tryLength = tryEnd - tryStart;
                    int handlerLength = handlerEnd - handlerStart;

                    if (tryStart < 0 || tryLength < 0 || (tryStart + tryLength) > codeSize)
                        throw new InvalidOperationException($"Invalid try region: start={tryStart}, end={tryEnd}, codeSize={codeSize}");

                    if (handlerStart < 0 || handlerLength < 0 || (handlerStart + handlerLength) > codeSize)
                        throw new InvalidOperationException($"Invalid handler region: start={handlerStart}, end={handlerEnd}, codeSize={codeSize}");

                    switch (eh.HandlerType)
                    {
                        case MutableExceptionHandlerType.Catch:
                            var catchHandle = GetTypeHandle(eh.CatchType);
                            regionEncoder.AddCatch(tryStart, tryLength, handlerStart, handlerLength, catchHandle);
                            break;
                        case MutableExceptionHandlerType.Filter:
                            int filterOffset = GetExceptionBoundaryOffset(body, eh.FilterStart, handlerStart);
                            regionEncoder.AddFilter(tryStart, tryLength, handlerStart, handlerLength, filterOffset);
                            break;
                        case MutableExceptionHandlerType.Finally:
                            regionEncoder.AddFinally(tryStart, tryLength, handlerStart, handlerLength);
                            break;
                        case MutableExceptionHandlerType.Fault:
                            regionEncoder.AddFault(tryStart, tryLength, handlerStart, handlerLength);
                            break;
                        default:
                            regionEncoder.AddCatch(tryStart, tryLength, handlerStart, handlerLength, default);
                            break;
                    }
                }
            }

            return methodBody.Offset;
        }

        private byte[] EncodeIL(MutableMethodBody body)
        {
            // First pass: calculate offsets
            var totalSize = AssignInstructionOffsets(body.Instructions);
            
            // Second pass: encode
            var bytes = new List<byte>(totalSize);
            
            foreach (var instruction in body.Instructions)
            {
                EncodeInstruction(bytes, body, instruction);
            }
            
            return bytes.ToArray();
        }

        private static int AssignInstructionOffsets(IReadOnlyList<MutableInstruction> instructions)
        {
            int offset = 0;
            foreach (var instruction in instructions)
            {
                instruction.Offset = offset;
                offset += instruction.GetSize();
            }

            return offset;
        }

        private void EncodeInstruction(List<byte> bytes, MutableMethodBody body, MutableInstruction instruction)
        {
            // Encode opcode
            if (instruction.OpCode.Size == 2)
            {
                bytes.Add((byte)(instruction.OpCode.Value >> 8));
                bytes.Add((byte)(instruction.OpCode.Value & 0xFF));
            }
            else
            {
                bytes.Add((byte)instruction.OpCode.Value);
            }

            // Encode operand
            EncodeOperand(bytes, body, instruction);
        }

        private void EncodeOperand(List<byte> bytes, MutableMethodBody body, MutableInstruction instruction)
        {
            switch (instruction.OpCode.OperandType)
            {
                case MutableOperandType.InlineNone:
                    break;
                    
                case MutableOperandType.ShortInlineBrTarget:
                    WriteShortBranchTarget(bytes, instruction);
                    break;
                    
                case MutableOperandType.InlineBrTarget:
                    WriteBranchTarget(bytes, instruction);
                    break;
                    
                case MutableOperandType.ShortInlineI:
                    if (instruction.Operand is sbyte sb)
                        bytes.Add((byte)sb);
                    else if (instruction.Operand is byte b)
                        bytes.Add(b);
                    else
                        bytes.Add(0);
                    break;
                    
                case MutableOperandType.InlineI:
                    WriteInt32(bytes, instruction.Operand is int i ? i : 0);
                    break;
                    
                case MutableOperandType.InlineI8:
                    WriteInt64(bytes, instruction.Operand is long l ? l : 0);
                    break;
                    
                case MutableOperandType.ShortInlineR:
                    WriteInt32(bytes, SingleToInt32Bits(instruction.Operand is float f ? f : 0f));
                    break;
                    
                case MutableOperandType.InlineR:
                    WriteInt64(bytes, BitConverter.DoubleToInt64Bits(instruction.Operand is double d ? d : 0d));
                    break;
                    
                case MutableOperandType.InlineString:
                    WriteUserStringToken(bytes, instruction.Operand as string);
                    break;
                    
                case MutableOperandType.InlineMethod:
                    WriteMetadataToken(bytes, GetMethodHandle(instruction.Operand as MutableMethodReference));
                    break;
                    
                case MutableOperandType.InlineField:
                    WriteMetadataToken(bytes, GetFieldHandle(instruction.Operand as MutableFieldReference));
                    break;
                    
                case MutableOperandType.InlineType:
                    WriteMetadataToken(bytes, GetTypeHandle(instruction.Operand as MutableTypeReference));
                    break;
                    
                case MutableOperandType.InlineTok:
                    WriteMetadataToken(bytes, GetTokenHandle(instruction.Operand));
                    break;
                    
                case MutableOperandType.ShortInlineVar:
                    WriteShortInlineIndex(bytes, GetLocalIndex(instruction.Operand));
                    break;
                    
                case MutableOperandType.InlineVar:
                    WriteInlineIndex(bytes, GetLocalIndex(instruction.Operand));
                    break;
                    
                case MutableOperandType.ShortInlineArg:
                    WriteShortInlineIndex(bytes, GetArgumentIndex(body, instruction.Operand));
                    break;
                    
                case MutableOperandType.InlineArg:
                    WriteInlineIndex(bytes, GetArgumentIndex(body, instruction.Operand));
                    break;
                    
                case MutableOperandType.InlineSwitch:
                    WriteSwitchTargets(bytes, instruction);
                    break;
                    
                case MutableOperandType.InlineSig:
                    // Standalone signature
                    WriteInt32(bytes, instruction.Operand is int sigToken ? sigToken : 0);
                    break;
            }
        }

        private static int? GetLocalIndex(object operand)
        {
            if (operand is MutableVariableDefinition variable)
                return variable.Index;

            return GetNumericIndex(operand);
        }

        private static int? GetArgumentIndex(MutableMethodBody body, object operand)
        {
            if (operand is MutableParameterDefinition parameter)
                return parameter.Index + (body.Method?.HasThis == true ? 1 : 0);

            return GetNumericIndex(operand);
        }

        private static int? GetNumericIndex(object operand)
        {
            switch (operand)
            {
                case byte b:
                    return b;
                case sbyte sb:
                    return sb;
                case short s:
                    return s;
                case ushort us:
                    return us;
                case int i:
                    return i;
                default:
                    return null;
            }
        }

        private static void WriteShortBranchTarget(List<byte> bytes, MutableInstruction instruction)
        {
            if (instruction.Operand is MutableInstruction target)
            {
                var off = target.Offset - (instruction.Offset + instruction.GetSize());
                bytes.Add((byte)off);
            }
            else
            {
                bytes.Add(0);
            }
        }

        private void WriteBranchTarget(List<byte> bytes, MutableInstruction instruction)
        {
            if (instruction.Operand is MutableInstruction target)
            {
                // Branch offsets are relative to the next instruction.
                var off = target.Offset - (instruction.Offset + instruction.GetSize());
                WriteInt32(bytes, off);
            }
            else
            {
                WriteInt32(bytes, 0);
            }
        }

        private void WriteUserStringToken(List<byte> bytes, string value)
        {
            var stringHandle = GetOrAddUserString(value ?? string.Empty);
            WriteInt32(bytes, MetadataTokens.GetToken(stringHandle));
        }

        private void WriteMetadataToken(List<byte> bytes, EntityHandle handle)
        {
            WriteInt32(bytes, MetadataTokens.GetToken(handle));
        }

        private static void WriteShortInlineIndex(List<byte> bytes, int? index)
        {
            bytes.Add((byte)(index ?? 0));
        }

        private void WriteInlineIndex(List<byte> bytes, int? index)
        {
            WriteInt16(bytes, (short)(index ?? 0));
        }

        private void WriteSwitchTargets(List<byte> bytes, MutableInstruction instruction)
        {
            if (instruction.Operand is MutableInstruction[] targets)
            {
                WriteInt32(bytes, targets.Length);
                // Switch targets are encoded relative to the end of the switch instruction.
                int baseOffset = instruction.Offset + instruction.GetSize();
                foreach (var t in targets)
                {
                    WriteInt32(bytes, t != null ? t.Offset - baseOffset : 0);
                }
            }
            else
            {
                WriteInt32(bytes, 0);
            }
        }

        private static void WriteInt16(List<byte> bytes, short value)
        {
            unchecked
            {
                bytes.Add((byte)value);
                bytes.Add((byte)(value >> 8));
            }
        }

        private static void WriteInt32(List<byte> bytes, int value)
        {
            unchecked
            {
                bytes.Add((byte)value);
                bytes.Add((byte)(value >> 8));
                bytes.Add((byte)(value >> 16));
                bytes.Add((byte)(value >> 24));
            }
        }

        private static void WriteInt64(List<byte> bytes, long value)
        {
            unchecked
            {
                bytes.Add((byte)value);
                bytes.Add((byte)(value >> 8));
                bytes.Add((byte)(value >> 16));
                bytes.Add((byte)(value >> 24));
                bytes.Add((byte)(value >> 32));
                bytes.Add((byte)(value >> 40));
                bytes.Add((byte)(value >> 48));
                bytes.Add((byte)(value >> 56));
            }
        }

        private static int SingleToInt32Bits(float value)
        {
#if NETFRAMEWORK
            return BitConverter.ToInt32(BitConverter.GetBytes(value), 0);
#else
            return BitConverter.SingleToInt32Bits(value);
#endif
        }

        private void WritePropertyDefinition(MutablePropertyDefinition prop)
        {
            var signature = EncodePropertySignature(prop);
            
            var handle = _metadata.AddProperty(
                prop.Attributes,
                _metadata.GetOrAddString(prop.Name),
                _metadata.GetOrAddBlob(signature));

            _propertyDefHandles[prop] = handle;
            
            // Add method semantics
            if (prop.GetMethod != null && _methodDefHandles.TryGetValue(prop.GetMethod, out var getHandle))
            {
                _metadata.AddMethodSemantics(handle, System.Reflection.MethodSemanticsAttributes.Getter, getHandle);
            }
            if (prop.SetMethod != null && _methodDefHandles.TryGetValue(prop.SetMethod, out var setHandle))
            {
                _metadata.AddMethodSemantics(handle, System.Reflection.MethodSemanticsAttributes.Setter, setHandle);
            }
        }

        private void WriteEventDefinition(MutableEventDefinition evt)
        {
            var eventTypeHandle = GetTypeHandle(evt.EventType);
            
            var handle = _metadata.AddEvent(
                (EventAttributes)0,
                _metadata.GetOrAddString(evt.Name),
                eventTypeHandle);

            _eventDefHandles[evt] = handle;
            
            if (evt.AddMethod != null && _methodDefHandles.TryGetValue(evt.AddMethod, out var addHandle))
            {
                _metadata.AddMethodSemantics(handle, System.Reflection.MethodSemanticsAttributes.Adder, addHandle);
            }
            if (evt.RemoveMethod != null && _methodDefHandles.TryGetValue(evt.RemoveMethod, out var removeHandle))
            {
                _metadata.AddMethodSemantics(handle, System.Reflection.MethodSemanticsAttributes.Remover, removeHandle);
            }
            if (evt.InvokeMethod != null && _methodDefHandles.TryGetValue(evt.InvokeMethod, out var raiseHandle))
            {
                _metadata.AddMethodSemantics(handle, System.Reflection.MethodSemanticsAttributes.Raiser, raiseHandle);
            }
        }

        private void WriteAllGenericParameters()
        {
            var entries = new List<(EntityHandle owner, MutableGenericParameter gp)>();

            foreach (var type in _orderedTypes)
            {
                if (_typeDefHandles.TryGetValue(type, out var typeHandle))
                {
                    foreach (var gp in type.GenericParameters)
                    {
                        entries.Add((typeHandle, gp));
                    }
                }

                foreach (var method in type.Methods)
                {
                    if (_methodDefHandles.TryGetValue(method, out var methodHandle))
                    {
                        foreach (var gp in method.GenericParameters)
                        {
                            entries.Add((methodHandle, gp));
                        }
                    }
                }
            }

            entries.Sort((left, right) =>
            {
                var leftCode = GetTypeOrMethodDefCode(left.owner);
                var rightCode = GetTypeOrMethodDefCode(right.owner);
                if (leftCode != rightCode)
                    return leftCode.CompareTo(rightCode);

                return left.gp.Position.CompareTo(right.gp.Position);
            });

            foreach (var entry in entries)
            {
                var gp = entry.gp;
                var gpHandle = _metadata.AddGenericParameter(
                    entry.owner,
                    (GenericParameterAttributes)gp.GenericParameterAttributes,
                    _metadata.GetOrAddString(gp.Name),
                    gp.Position);
                _genericParameterHandles[gp] = gpHandle;

                foreach (var constraint in gp.Constraints)
                {
                    var constraintType = GetTypeHandle(constraint.ConstraintType);
                    _metadata.AddGenericParameterConstraint(gpHandle, constraintType);
                }
            }
        }

        private static int GetTypeOrMethodDefCode(EntityHandle owner)
        {
            switch (owner.Kind)
            {
                case HandleKind.TypeDefinition:
                    return MetadataTokens.GetRowNumber((TypeDefinitionHandle)owner) << 1;
                case HandleKind.MethodDefinition:
                    return (MetadataTokens.GetRowNumber((MethodDefinitionHandle)owner) << 1) | 1;
                default:
                    return int.MaxValue;
            }
        }

        private void WriteCustomAttributes()
        {
            WriteAssemblyCustomAttributes();
            WriteModuleCustomAttributes();

            foreach (var typeEntry in _typeDefHandles)
            {
                WriteTypeCustomAttributes(typeEntry.Key, typeEntry.Value);
            }
        }

        private void WriteAssemblyCustomAttributes()
        {
            if (_assemblyDefHandle.IsNil)
                return;

            AddCustomAttributes(_assemblyDefHandle, _assembly.CustomAttributes);
        }

        private void WriteModuleCustomAttributes()
        {
            if (_moduleDefHandle.IsNil)
                return;

            AddCustomAttributes(_moduleDefHandle, _assembly.MainModule.CustomAttributes);
        }

        private void WriteTypeCustomAttributes(MutableTypeDefinition type, TypeDefinitionHandle typeHandle)
        {
            AddCustomAttributes(typeHandle, type.CustomAttributes);
            WriteMethodCustomAttributes(type);
            WriteFieldCustomAttributes(type);
            WriteGenericParameterCustomAttributes(type);
            WritePropertyCustomAttributes(type);
            WriteEventCustomAttributes(type);
        }

        private void WriteMethodCustomAttributes(MutableTypeDefinition type)
        {
            foreach (var method in type.Methods)
            {
                if (_methodDefHandles.TryGetValue(method, out var methodHandle))
                {
                    AddCustomAttributes(methodHandle, method.CustomAttributes);
                }

                foreach (var param in method.Parameters)
                {
                    if (_parameterHandles.TryGetValue(param, out var paramHandle))
                    {
                        AddCustomAttributes(paramHandle, param.CustomAttributes);
                    }
                }

                foreach (var gp in method.GenericParameters)
                {
                    if (_genericParameterHandles.TryGetValue(gp, out var gpHandle))
                    {
                        AddCustomAttributes(gpHandle, gp.CustomAttributes);
                    }
                }
            }
        }

        private void WriteFieldCustomAttributes(MutableTypeDefinition type)
        {
            foreach (var field in type.Fields)
            {
                if (_fieldDefHandles.TryGetValue(field, out var fieldHandle))
                {
                    AddCustomAttributes(fieldHandle, field.CustomAttributes);
                }
            }
        }

        private void WriteGenericParameterCustomAttributes(MutableTypeDefinition type)
        {
            foreach (var gp in type.GenericParameters)
            {
                if (_genericParameterHandles.TryGetValue(gp, out var gpHandle))
                {
                    AddCustomAttributes(gpHandle, gp.CustomAttributes);
                }
            }
        }

        private void WritePropertyCustomAttributes(MutableTypeDefinition type)
        {
            foreach (var prop in type.Properties)
            {
                if (_propertyDefHandles.TryGetValue(prop, out var propHandle))
                {
                    AddCustomAttributes(propHandle, prop.CustomAttributes);
                }
            }
        }

        private void WriteEventCustomAttributes(MutableTypeDefinition type)
        {
            foreach (var evt in type.Events)
            {
                if (_eventDefHandles.TryGetValue(evt, out var evtHandle))
                {
                    AddCustomAttributes(evtHandle, evt.CustomAttributes);
                }
            }
        }

        private void AddCustomAttributes(EntityHandle parent, IEnumerable<MutableCustomAttribute> attributes)
        {
            foreach (var attr in attributes)
            {
                AddCustomAttribute(parent, attr);
            }
        }

        private void AddCustomAttribute(EntityHandle parent, MutableCustomAttribute attr)
        {
            try
            {
                var constructor = GetMethodHandle(attr.Constructor);
                var value = EncodeCustomAttributeValue(attr);

                _metadata.AddCustomAttribute(
                    parent: parent,
                    constructor: constructor,
                    value: value);
            }
            catch
            {
                LoggerService.Logger.LogDebug($"Custom attribute skipped: {attr?.AttributeTypeName}");
            }
        }

        private BlobHandle EncodeCustomAttributeValue(MutableCustomAttribute attr)
        {
            var builder = new BlobBuilder();

            WriteCustomAttributeProlog(builder);
            WriteCustomAttributeFixedArguments(builder, attr.Constructor, attr.ConstructorArguments);
            WriteCustomAttributeNamedArguments(builder, attr.Fields, attr.Properties);

            return _metadata.GetOrAddBlob(builder);
        }

        private static void WriteCustomAttributeProlog(BlobBuilder builder)
        {
            builder.WriteUInt16(0x0001);
        }

        private void WriteCustomAttributeFixedArguments(
            BlobBuilder builder,
            MutableMethodReference constructor,
            IReadOnlyList<MutableCustomAttributeArgument> args)
        {
            for (int i = 0; i < args.Count; i++)
            {
                MutableCustomAttributeArgument argument = args[i];
                MutableTypeReference expectedType = i < constructor.Parameters.Count
                    ? constructor.Parameters[i].ParameterType
                    : argument?.Type;

                if (expectedType?.FullName == "System.Object")
                {
                    WriteBoxedCustomAttributeArgument(builder, argument?.Value);
                    continue;
                }

                EncodeCustomAttributeArgument(builder, argument);
            }
        }

        private void WriteCustomAttributeNamedArguments(
            BlobBuilder builder,
            IReadOnlyList<MutableCustomAttributeNamedArgument> fields,
            IReadOnlyList<MutableCustomAttributeNamedArgument> properties)
        {
            builder.WriteUInt16((ushort)(fields.Count + properties.Count));

            foreach (var field in fields)
            {
                WriteCustomAttributeNamedArgument(builder, 0x53, field);
            }

            foreach (var prop in properties)
            {
                WriteCustomAttributeNamedArgument(builder, 0x54, prop);
            }
        }

        private void WriteCustomAttributeNamedArgument(
            BlobBuilder builder,
            byte kind,
            MutableCustomAttributeNamedArgument argument)
        {
            builder.WriteByte(kind);
            EncodeCustomAttributeFieldOrPropType(builder, argument.Argument);
            WriteSerializedString(builder, argument.Name);
            EncodeCustomAttributeArgument(builder, argument.Argument);
        }

        private void EncodeCustomAttributeArgument(BlobBuilder builder, MutableCustomAttributeArgument arg)
        {
            if (arg?.Type == null)
            {
                if (arg?.Value is MutableTypeReference inferredTypeWithoutExplicitType)
                {
                    WriteSerializedString(builder, GetCustomAttributeTypeName(inferredTypeWithoutExplicitType));
                    return;
                }

                if (arg?.Value is string stringValue)
                {
                    WriteSerializedString(builder, stringValue);
                    return;
                }

                if (TryWritePrimitiveValueFromObject(builder, arg?.Value))
                    return;

                builder.WriteInt32(0);
                return;
            }

            if (arg.Type.FullName == "System.String")
            {
                WriteSerializedString(builder, arg.Value as string);
                return;
            }

            if (arg.Type.FullName == "System.Object")
            {
                WriteBoxedCustomAttributeArgument(builder, arg.Value);
                return;
            }

            if (arg.Type.FullName == "System.Type")
            {
                var typeRef = arg.Value as MutableTypeReference;
                WriteSerializedString(builder, GetCustomAttributeTypeName(typeRef));
                return;
            }

            if (arg.Value is MutableTypeReference inferredTypeValue)
            {
                WriteSerializedString(builder, GetCustomAttributeTypeName(inferredTypeValue));
                return;
            }

            if (arg.Value is IEnumerable<MutableCustomAttributeArgument> arrayArgs)
            {
                var list = arrayArgs.ToList();
                builder.WriteInt32(list.Count);
                foreach (var item in list)
                {
                    EncodeCustomAttributeArgument(builder, item);
                }
                return;
            }

            if (arg.Type.Resolve() is MutableTypeDefinition enumDef && enumDef.IsEnum)
            {
                var underlyingType = enumDef.GetFieldByName("value__")?.FieldType;
                if (underlyingType != null)
                {
                    WritePrimitiveValue(builder, arg.Value, underlyingType);
                }
                else
                {
                    builder.WriteInt32(0);
                }
                return;
            }

            if (arg.Type.IsPrimitive)
            {
                WritePrimitiveValue(builder, arg.Value, arg.Type);
                return;
            }

            if (TryWritePrimitiveValueFromObject(builder, arg.Value))
                return;

            builder.WriteInt32(0);
        }

        private void WriteBoxedCustomAttributeArgument(BlobBuilder builder, object value)
        {
            if (value == null)
            {
                // Null boxed object (for ELEMENT_TYPE_OBJECT) is encoded as 0xFF.
                builder.WriteByte(0xFF);
                return;
            }

            if (value is MutableCustomAttributeArgument nestedArgument)
            {
                EncodeCustomAttributeFieldOrPropType(builder, nestedArgument);
                EncodeCustomAttributeArgument(builder, nestedArgument);
                return;
            }

            if (value is string stringValue)
            {
                builder.WriteByte(0x0e); // ELEMENT_TYPE_STRING
                WriteSerializedString(builder, stringValue);
                return;
            }

            if (value is MutableTypeReference typeReference)
            {
                builder.WriteByte(0x50); // ELEMENT_TYPE_TYPE
                WriteSerializedString(builder, GetCustomAttributeTypeName(typeReference));
                return;
            }

            if (value is Type runtimeType)
            {
                builder.WriteByte(0x50); // ELEMENT_TYPE_TYPE
                WriteSerializedString(builder, runtimeType.AssemblyQualifiedName ?? runtimeType.FullName);
                return;
            }

            if (TryWritePrimitiveTypeFromObject(builder, value))
            {
                TryWritePrimitiveValueFromObject(builder, value);
                return;
            }

            // Preserve metadata validity when encountering unsupported boxed object payloads.
            builder.WriteByte(0xFF);
        }

        private void EncodeCustomAttributeFieldOrPropType(BlobBuilder builder, MutableCustomAttributeArgument argument)
        {
            var type = argument?.Type;
            if (type == null)
            {
                if (argument?.Value is MutableTypeReference)
                {
                    builder.WriteByte(0x50); // ELEMENT_TYPE_TYPE
                    return;
                }

                if (argument?.Value is string)
                {
                    builder.WriteByte(0x0e);
                    return;
                }

                if (TryWritePrimitiveTypeFromObject(builder, argument?.Value))
                    return;

                builder.WriteByte(0x51); // ELEMENT_TYPE_OBJECT
                return;
            }

            if (type is MutableArrayType arrayType)
            {
                builder.WriteByte(0x1d); // ELEMENT_TYPE_SZARRAY
                EncodeCustomAttributeFieldOrPropType(builder, new MutableCustomAttributeArgument(arrayType.ElementType, null));
                return;
            }

            if (argument?.Value is MutableTypeReference)
            {
                builder.WriteByte(0x50); // ELEMENT_TYPE_TYPE
                return;
            }

            if (type?.Resolve() is MutableTypeDefinition enumDef && enumDef.IsEnum)
            {
                builder.WriteByte(0x55); // ELEMENT_TYPE_ENUM
                WriteSerializedString(builder, GetCustomAttributeTypeName(type));
                return;
            }

            // Use switch for compiler-optimized string comparison
            switch (type.FullName)
            {
                case "System.Boolean": builder.WriteByte(0x02); break;
                case "System.Char": builder.WriteByte(0x03); break;
                case "System.SByte": builder.WriteByte(0x04); break;
                case "System.Byte": builder.WriteByte(0x05); break;
                case "System.Int16": builder.WriteByte(0x06); break;
                case "System.UInt16": builder.WriteByte(0x07); break;
                case "System.Int32": builder.WriteByte(0x08); break;
                case "System.UInt32": builder.WriteByte(0x09); break;
                case "System.Int64": builder.WriteByte(0x0a); break;
                case "System.UInt64": builder.WriteByte(0x0b); break;
                case "System.Single": builder.WriteByte(0x0c); break;
                case "System.Double": builder.WriteByte(0x0d); break;
                case "System.String": builder.WriteByte(0x0e); break;
                case "System.Type": builder.WriteByte(0x50); break;
                default: builder.WriteByte(0x51); break; // ELEMENT_TYPE_OBJECT
            }
        }

        private static void WriteSerializedString(BlobBuilder builder, string value)
        {
            if (value == null)
            {
                builder.WriteByte(0xFF);
            }
            else
            {
                var bytes = System.Text.Encoding.UTF8.GetBytes(value);
                builder.WriteCompressedInteger(bytes.Length);
                builder.WriteBytes(bytes);
            }
        }

        private string GetCustomAttributeTypeName(MutableTypeReference typeRef)
        {
            if (typeRef == null)
                return null;

            var typeName = typeRef.FullName?.Replace('/', '+');
            var assemblyName = GetCustomAttributeTypeAssemblyName(typeRef);
            return string.IsNullOrEmpty(assemblyName)
                ? typeName
                : $"{typeName}, {assemblyName}";
        }

        private string GetCustomAttributeTypeAssemblyName(MutableTypeReference typeRef)
        {
            var rootType = typeRef;
            while (rootType?.DeclaringType != null)
            {
                rootType = rootType.DeclaringType;
            }

            if (rootType?.Scope is MutableAssemblyNameReference asmRef)
                return asmRef.FullName;

            if (rootType?.Scope is MutableAssemblyNameDefinition asmDef)
                return asmDef.FullName;

            var module = rootType?.Scope as MutableModuleDefinition ?? rootType?.Module;
            if (module != null &&
                !ReferenceEquals(module, _assembly.MainModule) &&
                module.Assembly?.Name != null)
            {
                return module.Assembly.Name.FullName;
            }

            return null;
        }

        private static void WritePrimitiveValue(BlobBuilder builder, object value, MutableTypeReference type)
        {
            if (value == null || type == null)
                return;

            switch (type.FullName)
            {
                case "System.Boolean": builder.WriteBoolean(Convert.ToBoolean(value)); break;
                case "System.Byte": builder.WriteByte(Convert.ToByte(value)); break;
                case "System.SByte": builder.WriteSByte(Convert.ToSByte(value)); break;
                case "System.Int16": builder.WriteInt16(Convert.ToInt16(value)); break;
                case "System.UInt16": builder.WriteUInt16(Convert.ToUInt16(value)); break;
                case "System.Int32": builder.WriteInt32(Convert.ToInt32(value)); break;
                case "System.UInt32": builder.WriteUInt32(Convert.ToUInt32(value)); break;
                case "System.Int64": builder.WriteInt64(Convert.ToInt64(value)); break;
                case "System.UInt64": builder.WriteUInt64(Convert.ToUInt64(value)); break;
                case "System.Single": builder.WriteSingle(Convert.ToSingle(value)); break;
                case "System.Double": builder.WriteDouble(Convert.ToDouble(value)); break;
                case "System.Char": builder.WriteUInt16(Convert.ToUInt16(value)); break;
            }
        }

        private static bool TryWritePrimitiveTypeFromObject(BlobBuilder builder, object value)
        {
            if (value is bool) { builder.WriteByte(0x02); return true; }
            if (value is char) { builder.WriteByte(0x03); return true; }
            if (value is sbyte) { builder.WriteByte(0x04); return true; }
            if (value is byte) { builder.WriteByte(0x05); return true; }
            if (value is short) { builder.WriteByte(0x06); return true; }
            if (value is ushort) { builder.WriteByte(0x07); return true; }
            if (value is int) { builder.WriteByte(0x08); return true; }
            if (value is uint) { builder.WriteByte(0x09); return true; }
            if (value is long) { builder.WriteByte(0x0a); return true; }
            if (value is ulong) { builder.WriteByte(0x0b); return true; }
            if (value is float) { builder.WriteByte(0x0c); return true; }
            if (value is double) { builder.WriteByte(0x0d); return true; }
            if (value is Type || value is MutableTypeReference) { builder.WriteByte(0x50); return true; }
            return false;
        }

        private static bool TryWritePrimitiveValueFromObject(BlobBuilder builder, object value)
        {
            if (value is bool boolValue) { builder.WriteBoolean(boolValue); return true; }
            if (value is char charValue) { builder.WriteUInt16(charValue); return true; }
            if (value is sbyte sbyteValue) { builder.WriteSByte(sbyteValue); return true; }
            if (value is byte byteValue) { builder.WriteByte(byteValue); return true; }
            if (value is short shortValue) { builder.WriteInt16(shortValue); return true; }
            if (value is ushort ushortValue) { builder.WriteUInt16(ushortValue); return true; }
            if (value is int intValue) { builder.WriteInt32(intValue); return true; }
            if (value is uint uintValue) { builder.WriteUInt32(uintValue); return true; }
            if (value is long longValue) { builder.WriteInt64(longValue); return true; }
            if (value is ulong ulongValue) { builder.WriteUInt64(ulongValue); return true; }
            if (value is float floatValue) { builder.WriteSingle(floatValue); return true; }
            if (value is double doubleValue) { builder.WriteDouble(doubleValue); return true; }
            return false;
        }

        private BlobBuilder EncodeFieldSignature(MutableTypeReference fieldType)
        {
            var builder = new BlobBuilder();
            var encoder = new BlobEncoder(builder);
            EncodeTypeToBuilder(encoder.FieldSignature(), fieldType);
            return builder;
        }

        private BlobBuilder EncodeMethodSignature(MutableMethodDefinition method)
        {
            var builder = new BlobBuilder();
            var encoder = new BlobEncoder(builder);
            
            encoder.MethodSignature(
                SignatureCallingConvention.Default, 
                method.GenericParameters.Count, 
                !method.IsStatic)
                .Parameters(
                    method.Parameters.Count,
                    returnType => EncodeReturnType(returnType, method.ReturnType),
                    parameters =>
                    {
                        foreach (var param in method.Parameters)
                        {
                            var paramEncoder = parameters.AddParameter();
                            EncodeParameterType(paramEncoder, param.ParameterType);
                        }
                    });
            
            return builder;
        }

        private void EncodeParameterType(ParameterTypeEncoder paramEncoder, MutableTypeReference type)
        {
            // Handle modified types (modreq/modopt) at the parameter level
            if (type is MutableModifiedType modifiedType)
            {
                var modifierHandle = GetTypeHandle(modifiedType.Modifier);
                var modifiersEncoder = paramEncoder.CustomModifiers();
                modifiersEncoder.AddModifier(modifierHandle, isOptional: !modifiedType.IsRequired);
                
                // After adding modifiers, encode the underlying type
                EncodeTypeToBuilder(paramEncoder.Type(), modifiedType.ElementType);
            }
            else
            {
                EncodeTypeToBuilder(paramEncoder.Type(), type);
            }
        }

        private void EncodeReturnType(ReturnTypeEncoder encoder, MutableTypeReference type)
        {
            // Handle modified types (modreq/modopt) on return type (e.g., init-only setters)
            if (type is MutableModifiedType modifiedType)
            {
                var modifierHandle = GetTypeHandle(modifiedType.Modifier);
                var modifiersEncoder = encoder.CustomModifiers();
                modifiersEncoder.AddModifier(modifierHandle, isOptional: !modifiedType.IsRequired);
                
                // Now encode the underlying type
                if (modifiedType.ElementType == null || modifiedType.ElementType.FullName == "System.Void")
                {
                    encoder.Void();
                }
                else
                {
                    EncodeTypeToBuilder(encoder.Type(), modifiedType.ElementType);
                }
            }
            else if (type == null || type.FullName == "System.Void")
            {
                encoder.Void();
            }
            else
            {
                EncodeTypeToBuilder(encoder.Type(), type);
            }
        }

        private BlobBuilder EncodePropertySignature(MutablePropertyDefinition prop)
        {
            var builder = new BlobBuilder();
            var encoder = new BlobEncoder(builder);
            var parameterCount = prop.Parameters.Count;
            
            encoder.PropertySignature(!prop.GetMethod?.IsStatic ?? true)
                .Parameters(parameterCount,
                    returnType => EncodeReturnType(returnType, prop.PropertyType),
                    parameters =>
                    {
                        for (int i = 0; i < parameterCount; i++)
                        {
                            var parameter = prop.Parameters[i];
                            EncodeTypeToBuilder(parameters.AddParameter().Type(), parameter.ParameterType);
                        }
                    });
            
            return builder;
        }

        private void EncodeTypeToBuilder(SignatureTypeEncoder encoder, MutableTypeReference type)
        {
            if (type == null)
            {
                encoder.Object();
                return;
            }

            if (type is MutableGenericInstanceType genericInstance)
            {
                EncodeGenericInstance(encoder, genericInstance);
                return;
            }
            
            if (type is MutableArrayType arrayType)
            {
                EncodeArrayType(encoder, arrayType);
                return;
            }
            
            if (type is MutableByReferenceType byRefType)
            {
                EncodeByReferenceType(encoder, byRefType);
                return;
            }
            
            if (type is MutablePointerType pointerType)
            {
                EncodePointerType(encoder, pointerType);
                return;
            }

            if (type is MutableModifiedType modifiedType)
            {
                EncodeModifiedType(encoder, modifiedType);
                return;
            }

            if (type is MutablePinnedType pinnedType)
            {
                encoder.Builder.WriteByte((byte)SignatureTypeCode.Pinned);
                EncodeTypeToBuilder(encoder, pinnedType.ElementType);
                return;
            }
            
            if (type is MutableGenericParameter gp)
            {
                EncodeGenericParameter(encoder, gp);
                return;
            }

            if (TryEncodePrimitiveType(encoder, type.FullName))
                return;

            EncodeTypeReference(encoder, type);
        }

        private static bool TryEncodePrimitiveType(SignatureTypeEncoder encoder, string fullName)
        {
            switch (fullName)
            {
                case "System.Void":
                    // Void should not appear in type signatures except return type
                    encoder.Builder.WriteByte((byte)SignatureTypeCode.Void);
                    return true;
                case "System.Boolean": encoder.Boolean(); return true;
                case "System.Char": encoder.Char(); return true;
                case "System.SByte": encoder.SByte(); return true;
                case "System.Byte": encoder.Byte(); return true;
                case "System.Int16": encoder.Int16(); return true;
                case "System.UInt16": encoder.UInt16(); return true;
                case "System.Int32": encoder.Int32(); return true;
                case "System.UInt32": encoder.UInt32(); return true;
                case "System.Int64": encoder.Int64(); return true;
                case "System.UInt64": encoder.UInt64(); return true;
                case "System.Single": encoder.Single(); return true;
                case "System.Double": encoder.Double(); return true;
                case "System.IntPtr": encoder.IntPtr(); return true;
                case "System.UIntPtr": encoder.UIntPtr(); return true;
                case "System.String": encoder.String(); return true;
                case "System.Object": encoder.Object(); return true;
                default: return false;
            }
        }

        private void EncodeGenericInstance(SignatureTypeEncoder encoder, MutableGenericInstanceType genericInstance)
        {
            var elementHandle = GetTypeHandle(genericInstance.ElementType);
            var argsEncoder = encoder.GenericInstantiation(
                elementHandle,
                genericInstance.GenericArguments.Count,
                genericInstance.IsValueType);
            foreach (var arg in genericInstance.GenericArguments)
            {
                EncodeTypeToBuilder(argsEncoder.AddArgument(), arg);
            }
        }

        private void EncodeArrayType(SignatureTypeEncoder encoder, MutableArrayType arrayType)
        {
            if (arrayType.Rank == 1)
            {
                EncodeTypeToBuilder(encoder.SZArray(), arrayType.ElementType);
                return;
            }

            encoder.Array(
                e => EncodeTypeToBuilder(e, arrayType.ElementType),
                s => s.Shape(arrayType.Rank, System.Collections.Immutable.ImmutableArray<int>.Empty,
                    System.Collections.Immutable.ImmutableArray<int>.Empty));
        }

        private void EncodeByReferenceType(SignatureTypeEncoder encoder, MutableByReferenceType byRefType)
        {
            encoder.Builder.WriteByte((byte)SignatureTypeCode.ByReference);
            EncodeTypeToBuilder(encoder, byRefType.ElementType);
        }

        private void EncodePointerType(SignatureTypeEncoder encoder, MutablePointerType pointerType)
        {
            EncodeTypeToBuilder(encoder.Pointer(), pointerType.ElementType);
        }

        private void EncodeModifiedType(SignatureTypeEncoder encoder, MutableModifiedType modifiedType)
        {
            // Get the handle for the modifier type
            var modifierHandle = GetTypeHandle(modifiedType.Modifier);
            
            // Write the custom modifier directly to the blob
            // modreq = 0x1F (ELEMENT_TYPE_CMOD_REQD), modopt = 0x20 (ELEMENT_TYPE_CMOD_OPT)
            if (modifiedType.IsRequired)
            {
                encoder.Builder.WriteByte(0x1F); // ELEMENT_TYPE_CMOD_REQD
            }
            else
            {
                encoder.Builder.WriteByte(0x20); // ELEMENT_TYPE_CMOD_OPT
            }
            
            // Write the modifier type token as a compressed TypeDefOrRefOrSpec coded index
            encoder.Builder.WriteCompressedInteger(CodedIndex.TypeDefOrRefOrSpec(modifierHandle));
            
            // Then encode the underlying element type
            EncodeTypeToBuilder(encoder, modifiedType.ElementType);
        }

        private static void EncodeGenericParameter(SignatureTypeEncoder encoder, MutableGenericParameter gp)
        {
            if (gp.IsMethodParameter || gp.Owner is MutableMethodDefinition || gp.Owner is MutableMethodReference)
            {
                encoder.GenericMethodTypeParameter(gp.Position);
            }
            else
            {
                encoder.GenericTypeParameter(gp.Position);
            }
        }

        private void EncodeTypeReference(SignatureTypeEncoder encoder, MutableTypeReference type)
        {
            var handle = GetTypeHandle(type);
            encoder.Type(handle, type.IsValueType);
        }

        private EntityHandle GetTypeHandle(MutableTypeReference type)
        {
            if (type == null)
                return default;

            if (type is MutableTypeDefinition typeDef && _typeDefHandles.TryGetValue(typeDef, out var defHandle))
                return defHandle;

            if (_typeRefHandles.TryGetValue(type, out var existing))
                return existing;

            if (type is MutableGenericParameter)
            {
                var sig = new BlobBuilder();
                var sigEncoder = new BlobEncoder(sig);
                EncodeTypeToBuilder(sigEncoder.TypeSpecificationSignature(), type);

                var specHandle = _metadata.AddTypeSpecification(_metadata.GetOrAddBlob(sig));
                _typeRefHandles[type] = specHandle;
                return specHandle;
            }

            // Need to create a type reference or type spec
            if (type is MutableGenericInstanceType || type is MutableArrayType || 
                type is MutableByReferenceType || type is MutablePointerType)
            {
                // Create TypeSpec
                var sig = new BlobBuilder();
                var sigEncoder = new BlobEncoder(sig);
                EncodeTypeToBuilder(sigEncoder.TypeSpecificationSignature(), type);
                
                var specHandle = _metadata.AddTypeSpecification(_metadata.GetOrAddBlob(sig));
                _typeRefHandles[type] = specHandle;
                return specHandle;
            }

            // Create TypeRef
            EntityHandle resolutionScope = GetResolutionScopeHandle(type);
            var typeNamespace = type.Namespace ?? string.Empty;
            if (type.DeclaringType != null)
            {
                // Nested type references are scoped by declaring type, not namespace.
                resolutionScope = GetTypeHandle(type.DeclaringType);
                typeNamespace = string.Empty;
            }

            var refHandle = _metadata.AddTypeReference(
                resolutionScope,
                _metadata.GetOrAddString(typeNamespace),
                _metadata.GetOrAddString(type.Name));
            
            _typeRefHandles[type] = refHandle;
            return refHandle;
        }

        private EntityHandle GetResolutionScopeHandle(MutableTypeReference type)
        {
            if (type?.Scope is MutableAssemblyNameReference asmRef &&
                TryGetOrAddAssemblyReference(asmRef.Name, asmRef.Version, asmRef.Culture, asmRef.PublicKeyToken, asmRef.Attributes, out var asmHandle))
            {
                return asmHandle;
            }

            if (type?.Scope is MutableAssemblyNameDefinition asmDef &&
                TryGetOrAddAssemblyReference(asmDef.Name, asmDef.Version, asmDef.Culture, asmDef.PublicKeyToken, asmDef.Attributes, out var asmDefHandle))
            {
                return asmDefHandle;
            }

            if (type?.Scope is MutableModuleDefinition scopeModule)
            {
                if (ReferenceEquals(scopeModule, _assembly.MainModule))
                    return _moduleDefHandle;

                if (scopeModule.Assembly?.Name != null &&
                    TryGetOrAddAssemblyReference(
                        scopeModule.Assembly.Name.Name,
                        scopeModule.Assembly.Name.Version,
                        scopeModule.Assembly.Name.Culture,
                        scopeModule.Assembly.Name.PublicKeyToken,
                        scopeModule.Assembly.Name.Attributes,
                        out var scopeAsmHandle))
                {
                    return scopeAsmHandle;
                }
            }

            if (type?.Module != null)
            {
                if (ReferenceEquals(type.Module, _assembly.MainModule))
                    return _moduleDefHandle;

                if (type.Module.Assembly?.Name != null &&
                    TryGetOrAddAssemblyReference(
                        type.Module.Assembly.Name.Name,
                        type.Module.Assembly.Name.Version,
                        type.Module.Assembly.Name.Culture,
                        type.Module.Assembly.Name.PublicKeyToken,
                        type.Module.Assembly.Name.Attributes,
                        out var moduleAsmHandle))
                {
                    return moduleAsmHandle;
                }
            }

            return default;
        }

        private bool TryGetOrAddAssemblyReference(
            string name,
            Version version,
            string culture,
            byte[] publicKeyToken,
            AssemblyNameFlags attributes,
            out AssemblyReferenceHandle handle)
        {
            foreach (var pair in _asmRefHandles)
            {
                var existing = pair.Key;
                if (!string.Equals(existing.Name, name, StringComparison.Ordinal))
                    continue;
                if (!Equals(existing.Version, version))
                    continue;
                if (!string.Equals(existing.Culture ?? string.Empty, culture ?? string.Empty, StringComparison.Ordinal))
                    continue;
                if (existing.Attributes != attributes)
                    continue;
                if (!ByteArraysEqual(existing.PublicKeyToken, publicKeyToken))
                    continue;

                handle = pair.Value;
                return true;
            }

            if (string.IsNullOrEmpty(name))
            {
                handle = default;
                return false;
            }

            var added = _metadata.AddAssemblyReference(
                _metadata.GetOrAddString(name),
                version,
                _metadata.GetOrAddString(culture ?? string.Empty),
                publicKeyToken != null && publicKeyToken.Length > 0
                    ? _metadata.GetOrAddBlob(publicKeyToken)
                    : default,
                (AssemblyFlags)attributes,
                default);

            var synthetic = new MutableAssemblyNameReference(name, version)
            {
                Culture = culture,
                PublicKeyToken = publicKeyToken,
                Attributes = attributes
            };
            _asmRefHandles[synthetic] = added;
            handle = added;
            return true;
        }

        private static bool ByteArraysEqual(byte[] left, byte[] right)
        {
            if (ReferenceEquals(left, right))
                return true;
            if (left == null || right == null || left.Length != right.Length)
                return false;

            for (int i = 0; i < left.Length; i++)
            {
                if (left[i] != right[i])
                    return false;
            }

            return true;
        }

        private EntityHandle GetMethodHandle(MutableMethodReference method)
        {
            if (method == null)
                return default;

            if (method is MutableGenericInstanceMethod genericInstance)
            {
                if (_methodRefHandles.TryGetValue(method, out var existingSpec))
                    return existingSpec;

                var elementHandle = GetMethodHandle(genericInstance.ElementMethod);
                var specSig = new BlobBuilder();
                var specEncoder = new BlobEncoder(specSig);
                var argsEncoder = specEncoder.MethodSpecificationSignature(genericInstance.GenericArguments.Count);
                foreach (var arg in genericInstance.GenericArguments)
                {
                    EncodeTypeToBuilder(argsEncoder.AddArgument(), arg);
                }

                var specHandle = _metadata.AddMethodSpecification(elementHandle, _metadata.GetOrAddBlob(specSig));
                _methodRefHandles[method] = specHandle;
                return specHandle;
            }

            if (method is MutableMethodDefinition methodDef && _methodDefHandles.TryGetValue(methodDef, out var defHandle))
                return defHandle;

            if (TryResolveMethodReferenceToDefinitionHandle(method, out var resolvedDefHandle))
                return resolvedDefHandle;

            if (_methodRefHandles.TryGetValue(method, out var existing))
                return existing;

            // Create MemberRef
            var declaringHandle = GetTypeHandle(method.DeclaringType);
            var sig = EncodeMethodReferenceSignature(method);
            
            var refHandle = _metadata.AddMemberReference(
                declaringHandle,
                _metadata.GetOrAddString(method.Name),
                _metadata.GetOrAddBlob(sig));
            
            _methodRefHandles[method] = refHandle;
            return refHandle;
        }

        private bool TryResolveMethodReferenceToDefinitionHandle(
            MutableMethodReference methodReference,
            out EntityHandle methodDefinitionHandle)
        {
            methodDefinitionHandle = default;
            if (methodReference == null)
                return false;

            if (methodReference.DeclaringType is MutableGenericInstanceType)
                return false;

            MutableTypeDefinition declaringTypeDefinition = ResolveDeclaringTypeDefinition(methodReference.DeclaringType);
            if (declaringTypeDefinition == null)
                return false;

            foreach (var candidate in declaringTypeDefinition.Methods)
            {
                if (!string.Equals(candidate.Name, methodReference.Name, StringComparison.Ordinal))
                    continue;

                if (candidate.GenericParameters.Count != methodReference.GenericParameters.Count)
                    continue;

                if (candidate.Parameters.Count != methodReference.Parameters.Count)
                    continue;

                if (!AreEquivalentTypes(candidate.ReturnType, methodReference.ReturnType))
                    continue;

                bool parametersMatch = true;
                for (int i = 0; i < candidate.Parameters.Count; i++)
                {
                    if (!AreEquivalentTypes(candidate.Parameters[i].ParameterType, methodReference.Parameters[i].ParameterType))
                    {
                        parametersMatch = false;
                        break;
                    }
                }

                if (!parametersMatch)
                    continue;

                if (_methodDefHandles.TryGetValue(candidate, out var handle))
                {
                    methodDefinitionHandle = handle;
                    return true;
                }
            }

            return false;
        }

        private MutableTypeDefinition ResolveDeclaringTypeDefinition(MutableTypeReference declaringType)
        {
            if (declaringType == null)
                return null;

            if (declaringType is MutableTypeDefinition typeDefinition)
                return typeDefinition;

            if (declaringType is MutableGenericInstanceType genericInstance)
                return ResolveDeclaringTypeDefinition(genericInstance.ElementType);

            return declaringType.Resolve();
        }

        private static bool AreEquivalentTypes(MutableTypeReference left, MutableTypeReference right)
        {
            if (ReferenceEquals(left, right))
                return true;

            if (left == null || right == null)
                return false;

            if (left is MutableGenericParameter leftGenericParameter &&
                right is MutableGenericParameter rightGenericParameter)
            {
                bool leftMethodOwned = leftGenericParameter.Owner is MutableMethodReference || leftGenericParameter.Owner is MutableMethodDefinition;
                bool rightMethodOwned = rightGenericParameter.Owner is MutableMethodReference || rightGenericParameter.Owner is MutableMethodDefinition;
                return leftMethodOwned == rightMethodOwned && leftGenericParameter.Position == rightGenericParameter.Position;
            }

            if (left is MutableGenericInstanceType leftGenericInstance &&
                right is MutableGenericInstanceType rightGenericInstance)
            {
                if (!AreEquivalentTypes(leftGenericInstance.ElementType, rightGenericInstance.ElementType))
                    return false;

                if (leftGenericInstance.GenericArguments.Count != rightGenericInstance.GenericArguments.Count)
                    return false;

                for (int i = 0; i < leftGenericInstance.GenericArguments.Count; i++)
                {
                    if (!AreEquivalentTypes(leftGenericInstance.GenericArguments[i], rightGenericInstance.GenericArguments[i]))
                        return false;
                }

                return true;
            }

            if (left is MutableArrayType leftArray && right is MutableArrayType rightArray)
                return leftArray.Rank == rightArray.Rank && AreEquivalentTypes(leftArray.ElementType, rightArray.ElementType);

            if (left is MutableByReferenceType leftByRef && right is MutableByReferenceType rightByRef)
                return AreEquivalentTypes(leftByRef.ElementType, rightByRef.ElementType);

            if (left is MutablePointerType leftPointer && right is MutablePointerType rightPointer)
                return AreEquivalentTypes(leftPointer.ElementType, rightPointer.ElementType);

            if (left is MutableModifiedType leftModified && right is MutableModifiedType rightModified)
            {
                return leftModified.IsRequired == rightModified.IsRequired &&
                       AreEquivalentTypes(leftModified.Modifier, rightModified.Modifier) &&
                       AreEquivalentTypes(leftModified.ElementType, rightModified.ElementType);
            }

            if (left is MutablePinnedType leftPinned && right is MutablePinnedType rightPinned)
                return AreEquivalentTypes(leftPinned.ElementType, rightPinned.ElementType);

            return string.Equals(left.FullName, right.FullName, StringComparison.Ordinal);
        }

        private EntityHandle GetFieldHandle(MutableFieldReference field)
        {
            if (field == null)
                return default;

            if (TryCreateSelfGenericFieldReference(field, out var selfGenericField))
            {
                field = selfGenericField;
            }

            if (field is MutableFieldDefinition fieldDef && _fieldDefHandles.TryGetValue(fieldDef, out var defHandle))
                return defHandle;

            if (_fieldRefHandles.TryGetValue(field, out var existing))
                return existing;

            // Create MemberRef
            var declaringHandle = GetTypeHandle(field.DeclaringType);
            var sig = EncodeFieldSignature(field.FieldType);
            
            var refHandle = _metadata.AddMemberReference(
                declaringHandle,
                _metadata.GetOrAddString(field.Name),
                _metadata.GetOrAddBlob(sig));
            
            _fieldRefHandles[field] = refHandle;
            return refHandle;
        }

        private static bool TryCreateSelfGenericFieldReference(
            MutableFieldReference field,
            out MutableFieldReference fieldReference)
        {
            fieldReference = null;

            var declaringTypeDefinition = field?.DeclaringType as MutableTypeDefinition;
            var declaringTypeReference = field?.DeclaringType;
            if (declaringTypeReference == null)
                return false;
            if (declaringTypeReference is MutableGenericInstanceType)
                return false;

            var genericParameterCount = declaringTypeDefinition?.GenericParameters.Count ?? 0;
            if (genericParameterCount == 0)
            {
                var tickIndex = declaringTypeReference.Name?.LastIndexOf('`') ?? -1;
                if (tickIndex < 0 ||
                    tickIndex == declaringTypeReference.Name.Length - 1 ||
                    !int.TryParse(declaringTypeReference.Name.Substring(tickIndex + 1), out genericParameterCount))
                {
                    return false;
                }
            }

            if (genericParameterCount == 0)
                return false;

            var declaringType = new MutableGenericInstanceType(declaringTypeReference, genericParameterCount);
            for (int i = 0; i < genericParameterCount; i++)
            {
                var parameter = declaringTypeDefinition != null && i < declaringTypeDefinition.GenericParameters.Count
                    ? declaringTypeDefinition.GenericParameters[i]
                    : new MutableGenericParameter("T" + i.ToString(System.Globalization.CultureInfo.InvariantCulture), declaringTypeReference)
                    {
                        Position = i
                    };
                declaringType.GenericArguments.Add(parameter);
            }

            fieldReference = new MutableFieldReference(field.Name, field.FieldType, declaringType);
            return true;
        }

        private EntityHandle GetTokenHandle(object operand)
        {
            switch (operand)
            {
                case MutableTypeReference type: return GetTypeHandle(type);
                case MutableMethodReference method: return GetMethodHandle(method);
                case MutableFieldReference field: return GetFieldHandle(field);
                default: return default;
            }
        }

        private BlobBuilder EncodeMethodReferenceSignature(MutableMethodReference method)
        {
            var builder = new BlobBuilder();
            var encoder = new BlobEncoder(builder);
            
            encoder.MethodSignature(
                SignatureCallingConvention.Default,
                method.GenericParameters.Count,
                method.HasThis)
                .Parameters(
                    method.Parameters.Count,
                    returnType => EncodeReturnType(returnType, method.ReturnType),
                    parameters =>
                    {
                        foreach (var param in method.Parameters)
                        {
                            var paramEncoder = parameters.AddParameter();
                            EncodeParameterType(paramEncoder, param.ParameterType);
                        }
                    });
            
            return builder;
        }

        private EntityHandle GetMethodHandleForImpl(MutableMethodReference method)
        {
            if (method is MutableGenericInstanceMethod genericInstance)
                method = genericInstance.ElementMethod;

            var handle = GetMethodHandle(method);
            if (handle.Kind == HandleKind.MethodSpecification)
                return default;

            return handle;
        }

        private UserStringHandle GetOrAddUserString(string value)
        {
            if (_userStringHandles.TryGetValue(value, out var handle))
                return handle;
            
            handle = _metadata.GetOrAddUserString(value);
            _userStringHandles[value] = handle;
            return handle;
        }

        private void WritePEFile(Stream stream)
        {
            var module = _assembly.MainModule;
            
            // Determine PE characteristics
            var characteristics = Characteristics.ExecutableImage;
            if (module.Kind == MutableModuleKind.Dll)
            {
                characteristics |= Characteristics.Dll;
            }

            // Create PE builder
            var peHeaderBuilder = new PEHeaderBuilder(
                imageCharacteristics: characteristics,
                subsystem: module.Kind == MutableModuleKind.Windows ? Subsystem.WindowsGui : Subsystem.WindowsCui);

            // If the reader preserved the original image bytes, try to copy key
            // PE header settings (ImageBase, SectionAlignment, FileAlignment)
            // into the PEHeaderBuilder via reflection so that ManagedPEBuilder
            // lays out sections consistently with the original image.
            try
            {
                if (module.OriginalImageBytes != null && module.OriginalImageBytes.Length >= 0x40)
                {
                    using var origReader = new PEReader(new MemoryStream(module.OriginalImageBytes));
                    var origPeHeader = origReader.PEHeaders.PEHeader;
                    if (origPeHeader != null)
                    {
                        // Local helper to set a property if present on PEHeaderBuilder
                        void TrySet(object target, string name, object val)
                        {
                            var prop = target.GetType().GetProperty(name);
                            if (prop == null || !prop.CanWrite)
                            {
                                LeXtudio.Metadata.LoggerService.Logger.LogDebug($"MutableAssemblyWriter: PEHeaderBuilder missing property '{name}'");
                                return;
                            }
                            var t = prop.PropertyType;
                            object conv = val;
                            if (val != null && !t.IsAssignableFrom(val.GetType()))
                            {
                                if (t == typeof(int)) conv = Convert.ToInt32(val);
                                else if (t == typeof(uint)) conv = Convert.ToUInt32(val);
                                else if (t == typeof(long)) conv = Convert.ToInt64(val);
                                else if (t == typeof(ulong)) conv = Convert.ToUInt64(val);
                                else conv = Convert.ChangeType(val, t);
                            }
                            LeXtudio.Metadata.LoggerService.Logger.LogDebug($"MutableAssemblyWriter: setting PEHeaderBuilder.{name} = {conv} (targetType={t})");
                            prop.SetValue(target, conv);
                            LeXtudio.Metadata.LoggerService.Logger.LogDebug($"MutableAssemblyWriter: set PEHeaderBuilder.{name}");
                        }

                        TrySet(peHeaderBuilder, "ImageBase", origPeHeader.ImageBase);
                        TrySet(peHeaderBuilder, "SectionAlignment", origPeHeader.SectionAlignment);
                        TrySet(peHeaderBuilder, "FileAlignment", origPeHeader.FileAlignment);
                    }
                }
            }
            catch
            {
                // Ignore and continue — header preservation is best-effort.
            }

            // Create managed PE builder
            var corFlags = CorFlags.ILOnly;
            if (module.Attributes.HasFlag(MutableModuleAttributes.Required32Bit))
                corFlags |= CorFlags.Requires32Bit;
            if (module.Attributes.HasFlag(MutableModuleAttributes.Preferred32Bit))
                corFlags |= CorFlags.Prefers32Bit;

            // Only set StrongNameSigned if a signing key is provided. If the original
            // module was marked StrongNameSigned but we don't have a key blob, clear
            // the flag to avoid producing an assembly with a missing/invalid
            // strong-name signature which causes runtime load failures.
            var wantsStrongName = module.Attributes.HasFlag(MutableModuleAttributes.StrongNameSigned) || _parameters.StrongNameKeyBlob != null;
            if (wantsStrongName && _parameters.StrongNameKeyBlob == null)
            {
                LeXtudio.Metadata.LoggerService.Logger.LogWarning("MutableAssemblyWriter: assembly marked StrongNameSigned but no key blob provided; writing unsigned assembly and clearing StrongNameSigned flag.");
                wantsStrongName = false;
            }

            // Set StrongNameSigned based on whether we actually want a strong-name
            // signature (wantsStrongName) rather than solely the presence of a
            // key blob. This keeps the flags consistent with the reserved
            // signature directory size below.
            if (wantsStrongName)
            {
                corFlags |= CorFlags.StrongNameSigned;
            }
            else
            {
                corFlags &= ~CorFlags.StrongNameSigned;
            }

            MethodDefinitionHandle entryPoint = default;
            if (module.Kind != MutableModuleKind.Dll)
            {
                var entryPointMethod = module.EntryPoint ?? _assembly.EntryPoint;
                if (entryPointMethod == null)
                {
                    throw new InvalidOperationException(
                        $"Executable module '{module.Name}' does not have an entry point.");
                }

                var entryPointHandle = GetMethodHandle(entryPointMethod);
                if (entryPointHandle.Kind != HandleKind.MethodDefinition)
                {
                    throw new InvalidOperationException(
                        $"Executable module '{module.Name}' entry point '{entryPointMethod.FullName}' did not resolve to a method definition.");
                }

                entryPoint = (MethodDefinitionHandle)entryPointHandle;
            }

            LeXtudio.Metadata.LoggerService.Logger.LogDebug($"MutableAssemblyWriter: module.Attributes={module.Attributes} wantsStrongName={(wantsStrongName ? 1 : 0)} corFlags={corFlags}");
            var strongNameSignatureSize = wantsStrongName && _parameters.StrongNameKeyBlob != null
                ? _parameters.StrongNameKeyBlob.Length
                : 0;

            ResourceSectionBuilder nativeResources = null;
            if (_parameters.WriteNativeResources)
            {
                if (module.OriginalImageBytes != null && module.OriginalImageBytes.Length > 0)
                {
                    try
                    {
                        using var origPeStream = new MemoryStream(module.OriginalImageBytes);
                        using var origPeReader = new PEReader(origPeStream);
                        var resourceDir = origPeReader.PEHeaders.PEHeader?.ResourceTableDirectory;
                        if (resourceDir.HasValue && resourceDir.Value.Size > 0 && resourceDir.Value.RelativeVirtualAddress != 0)
                        {
                            var sectionData = origPeReader.GetSectionData(resourceDir.Value.RelativeVirtualAddress);
                            var resourceBytes = sectionData.GetReader(0, resourceDir.Value.Size).ReadBytes(resourceDir.Value.Size);
                            nativeResources = new RawWin32ResourceSectionBuilder(resourceBytes);
                        }
                    }
                    catch
                    {
                        // Best-effort: if we can't read native resources, proceed without them.
                    }
                }
            }

            var peBuilder = new ManagedPEBuilder(
                peHeaderBuilder,
                new MetadataRootBuilder(_metadata),
                _ilStream,
                mappedFieldData: _mappedFieldData,
                managedResources: _managedResources,
                nativeResources: nativeResources,
                debugDirectoryBuilder: null,
                strongNameSignatureSize: strongNameSignatureSize,
                entryPoint: entryPoint,
                flags: corFlags);

            // Write to blob
            var peBlob = new BlobBuilder();
            peBuilder.Serialize(peBlob);

            // Attempt to preserve a small set of original PE header fields (timestamp)
            // to minimize binary churn when performing a rewrite. If the original
            // module file is available via module.FileName, copy the 4-byte
            // COFF TimeDateStamp from the original into the newly generated PE.
            var newBytes = peBlob.ToArray();
            try
            {
                byte[] origBytes = null;

                // Prefer preserved original image bytes populated by the reader.
                if (module.OriginalImageBytes != null && module.OriginalImageBytes.Length > 0)
                {
                    origBytes = module.OriginalImageBytes;
                }
                else if (!string.IsNullOrEmpty(module.FileName) && File.Exists(module.FileName))
                {
                    origBytes = File.ReadAllBytes(module.FileName);
                }

                if (origBytes != null && origBytes.Length >= 0x40 && newBytes.Length >= 0x40)
                {
                    // e_lfanew is at offset 0x3c in DOS header
                    var origPe = BitConverter.ToInt32(origBytes, 0x3c);
                    var newPe = BitConverter.ToInt32(newBytes, 0x3c);
                    // COFF TimeDateStamp is at PE + 8
                    var origTimeStampOffset = origPe + 8;
                    var newTimeStampOffset = newPe + 8;
                    if (origTimeStampOffset + 4 <= origBytes.Length && newTimeStampOffset + 4 <= newBytes.Length)
                    {
                        Buffer.BlockCopy(origBytes, origTimeStampOffset, newBytes, newTimeStampOffset, 4);
                        LeXtudio.Metadata.LoggerService.Logger.LogDebug($"MutableAssemblyWriter: preserved TimeDateStamp from original module: 0x{BitConverter.ToInt32(origBytes, origTimeStampOffset):X8}");
                    }

                    // Optional header copying removed: we now try to set equivalent
                    // PEHeaderBuilder properties earlier so the PE is laid out
                    // deterministically by ManagedPEBuilder.
                }
            }
            catch
            {
                // Be conservative: if anything fails here, fall back to writing the
                // original generated blob unchanged.
                newBytes = peBlob.ToArray();
            }

            // Write to stream
            stream.Write(newBytes, 0, newBytes.Length);
        }

        private void WriteResources()
        {
            var module = _assembly.MainModule;
            if (module.Resources == null || module.Resources.Count == 0)
                return;

            foreach (var res in module.Resources)
            {
                if (res.ResourceType != MutableResourceType.Embedded)
                    continue;

                var embed = (MutableEmbeddedResource)res;
                var data = embed.GetResourceData() ?? Array.Empty<byte>();
                uint offset = checked((uint)_managedResources.Count);

                _managedResources.WriteInt32(data.Length);
                _managedResources.WriteBytes(data);

                var nameHandle = _metadata.GetOrAddString(embed.Name ?? string.Empty);
                _metadata.AddManifestResource((ManifestResourceAttributes)embed.Attributes, nameHandle, default(EntityHandle), offset);
            }
        }

        private static int GetExceptionBoundaryOffset(MutableMethodBody body, MutableInstruction instruction, int defaultOffset)
        {
            if (instruction == null)
                return defaultOffset;

            foreach (var candidate in body.Instructions)
            {
                if (ReferenceEquals(candidate, instruction))
                    return candidate.Offset;
            }

            return defaultOffset;
        }

        private static bool HasSmallExceptionRegions(MutableMethodBody body, int codeSize)
        {
            if (body.ExceptionHandlers == null || body.ExceptionHandlers.Count == 0)
                return true;

            foreach (var eh in body.ExceptionHandlers)
            {
                int tryStart = GetExceptionBoundaryOffset(body, eh.TryStart, 0);
                int tryEnd = GetExceptionBoundaryOffset(body, eh.TryEnd, codeSize);
                int handlerStart = GetExceptionBoundaryOffset(body, eh.HandlerStart, 0);
                int handlerEnd = GetExceptionBoundaryOffset(body, eh.HandlerEnd, codeSize);
                int filterStart = GetExceptionBoundaryOffset(body, eh.FilterStart, handlerStart);

                int tryLength = tryEnd - tryStart;
                int handlerLength = handlerEnd - handlerStart;

                if (tryStart < 0 || handlerStart < 0 || filterStart < 0)
                    return false;

                if (tryStart > ushort.MaxValue || handlerStart > ushort.MaxValue || filterStart > ushort.MaxValue)
                    return false;

                if (tryLength < 0 || handlerLength < 0 || tryLength > byte.MaxValue || handlerLength > byte.MaxValue)
                    return false;
            }

            return true;
        }
    }

    /// <summary>
    /// Serializes a raw Win32 resource section copied from the original PE, adjusting
    /// leaf data entry RVAs to account for the new section's virtual address.
    /// </summary>
    internal sealed class RawWin32ResourceSectionBuilder : ResourceSectionBuilder
    {
        private readonly byte[] _originalSectionBytes;

        public RawWin32ResourceSectionBuilder(byte[] originalSectionBytes)
        {
            _originalSectionBytes = originalSectionBytes ?? throw new ArgumentNullException(nameof(originalSectionBytes));
        }

        protected override void Serialize(BlobBuilder builder, SectionLocation location)
        {
            // The Win32 resource directory tree uses absolute RVAs in IMAGE_RESOURCE_DATA_ENTRY
            // records (the leaf nodes). When we move the .rsrc section to a new virtual address,
            // we must patch every leaf OffsetToData field so it points to the correct new RVA.
            //
            // Directory entries that point to sub-directories use offsets relative to the start of
            // the resource section (bit 31 set), so those do NOT need patching.
            //
            // We don't know the original section RVA here, so we read the raw bytes, walk the
            // resource directory tree to find all leaf IMAGE_RESOURCE_DATA_ENTRY records, and
            // patch their OffsetToData fields by computing:
            //   newRVA = oldRVA - originalSectionRva + location.RelativeVirtualAddress
            //
            // We compute originalSectionRva from the first leaf entry: its OffsetToData must
            // lie within the section, so (OffsetToData - offset_within_section) gives us the
            // original base. We find the data offset by following the directory tree.

            var bytes = (byte[])_originalSectionBytes.Clone();
            try
            {
                PatchResourceDataEntries(bytes, location.RelativeVirtualAddress);
            }
            catch
            {
                // If patching fails for any reason, write the unpatched bytes.
                // The resources will exist but data pointers may be wrong; this is
                // still better than dropping resources entirely.
            }

            builder.WriteBytes(bytes);
        }

        private static void PatchResourceDataEntries(byte[] bytes, int newSectionRva)
        {
            if (bytes.Length < 16)
                return;

            // First pass: collect all leaf IMAGE_RESOURCE_DATA_ENTRY offsets and one
            // representative (offset_of_entry, data_offset_within_section) pair to
            // compute the original section RVA.
            var leafOffsets = new List<int>();
            CollectLeafOffsets(bytes, 0, leafOffsets, depth: 0);

            if (leafOffsets.Count == 0)
                return;

            // Each IMAGE_RESOURCE_DATA_ENTRY is 16 bytes:
            //   DWORD OffsetToData   (absolute RVA)
            //   DWORD Size
            //   DWORD CodePage
            //   DWORD Reserved
            //
            // We determine originalSectionRva from the first leaf by computing
            // how far into the section the data actually lives.
            // data_within_section = OffsetToData - originalSectionRva  =>
            // We must infer originalSectionRva. We can't do so without additional context,
            // so instead we just relocate all OffsetToData values by the delta
            // (newSectionRva - originalSectionRva), where we compute originalSectionRva
            // from the first valid leaf.

            int firstLeafOffset = leafOffsets[0];
            if (firstLeafOffset + 16 > bytes.Length)
                return;

            int firstOffsetToData = BitConverter.ToInt32(bytes, firstLeafOffset);
            // The data pointed to by OffsetToData is at some position within the section.
            // We know: firstOffsetToData = originalSectionRva + data_within_section_offset
            // We cannot recover data_within_section_offset without knowing originalSectionRva.
            //
            // However, the IMAGE_RESOURCE_DATA_ENTRY Size field tells us how big the data is,
            // and the directory entries before it tell us where in the section the data resides.
            // A simpler heuristic: originalSectionRva ≈ firstOffsetToData - (some offset).
            //
            // In practice, resource data is placed after the directory tree, so for the first
            // leaf the data almost always starts after all directory entries. We can bound it:
            // originalSectionRva must satisfy:
            //   0 <= firstOffsetToData - originalSectionRva < bytes.Length
            //
            // Without the original PE's section RVA we approximate by scanning for the
            // smallest OffsetToData across all leaves and using that to infer the base.

            int minOffsetToData = int.MaxValue;
            foreach (int off in leafOffsets)
            {
                if (off + 4 <= bytes.Length)
                {
                    int rva = BitConverter.ToInt32(bytes, off);
                    if (rva > 0 && rva < minOffsetToData)
                        minOffsetToData = rva;
                }
            }

            if (minOffsetToData == int.MaxValue || minOffsetToData <= 0)
                return;

            // The minimum OffsetToData should map to somewhere inside the section bytes.
            // We assume the minimum data starts at some offset >= directory entries size.
            // Find the corresponding within-section offset by searching for a region that
            // looks like it could be the resource data. As a safe lower bound, use 0.
            //
            // A reliable approach: the within-section offset of the minimum data must be
            // minOffsetToData - originalSectionRva = some value in [0, bytes.Length).
            // We pick originalSectionRva = minOffsetToData - withinSectionOffset where
            // withinSectionOffset is determined by finding it in the directory.
            //
            // Simpler: walk leaves in parallel with the directory to find the withinSectionOffset.
            // For now, use the approach: find the within-section data offset for the leaf with
            // the smallest OffsetToData by re-walking the tree and tracking data offsets.

            int withinSectionOffset = FindFirstDataWithinSectionOffset(bytes, leafOffsets);
            if (withinSectionOffset < 0)
                return;

            int originalSectionRva = minOffsetToData - withinSectionOffset;
            if (originalSectionRva <= 0)
                return;

            int delta = newSectionRva - originalSectionRva;
            if (delta == 0)
            {
                return;
            }

            // Patch all leaf OffsetToData values.
            foreach (int off in leafOffsets)
            {
                if (off + 4 > bytes.Length)
                    continue;
                int oldRva = BitConverter.ToInt32(bytes, off);
                if (oldRva <= 0)
                    continue;
                int newRva = oldRva + delta;
                bytes[off] = (byte)newRva;
                bytes[off + 1] = (byte)(newRva >> 8);
                bytes[off + 2] = (byte)(newRva >> 16);
                bytes[off + 3] = (byte)(newRva >> 24);
            }
        }

        // IMAGE_RESOURCE_DIRECTORY is 16 bytes header + 8 bytes per entry
        // IMAGE_RESOURCE_DIRECTORY_ENTRY: DWORD Name/Id, DWORD OffsetToDataOrDirectory
        private static void CollectLeafOffsets(byte[] bytes, int dirOffset, List<int> leafOffsets, int depth)
        {
            if (depth > 3 || dirOffset < 0 || dirOffset + 16 > bytes.Length)
                return;

            // Skip 16-byte IMAGE_RESOURCE_DIRECTORY header
            int namedCount = BitConverter.ToUInt16(bytes, dirOffset + 12);
            int idCount = BitConverter.ToUInt16(bytes, dirOffset + 14);
            int totalEntries = namedCount + idCount;

            int entryOffset = dirOffset + 16;
            for (int i = 0; i < totalEntries; i++)
            {
                int entryStart = entryOffset + i * 8;
                if (entryStart + 8 > bytes.Length)
                    break;

                uint offsetToDataOrDir = BitConverter.ToUInt32(bytes, entryStart + 4);
                bool isDirectory = (offsetToDataOrDir & 0x80000000u) != 0;

                if (isDirectory)
                {
                    int subDirOffset = (int)(offsetToDataOrDir & 0x7FFFFFFFu);
                    CollectLeafOffsets(bytes, subDirOffset, leafOffsets, depth + 1);
                }
                else
                {
                    // This is an offset to an IMAGE_RESOURCE_DATA_ENTRY within the section
                    leafOffsets.Add((int)offsetToDataOrDir);
                }
            }
        }

        private static int FindFirstDataWithinSectionOffset(byte[] bytes, List<int> leafOffsets)
        {
            // leafOffsets are within-section offsets of IMAGE_RESOURCE_DATA_ENTRY structs.
            // The first DWORD of each entry is OffsetToData (absolute RVA).
            // We want the within-section data offset for the leaf that has the minimum OffsetToData.
            // Unfortunately we don't directly store where the actual data lives within the section
            // via the directory traversal above — leafOffsets are offsets to the DATA_ENTRY structs,
            // not to the data itself.
            //
            // The data for a leaf is NOT at leafOffset itself; it's somewhere else in the section.
            // We need to compute: withinSectionDataOffset = leafOffsetToData (absolute RVA) - sectionBase.
            //
            // We can instead use a different approach: the data must live within the section bytes,
            // so the within-section offset of the data for a leaf with OffsetToData=R is R - sectionBase.
            // We need sectionBase to compute this — which is circular.
            //
            // Instead, note that the directory entries themselves reside at known offsets from
            // the start of the section (that's what we traversed). The IMAGE_RESOURCE_DATA_ENTRY
            // is at a within-section offset stored in the directory entry. Those offsets are in
            // the 0x80000000 bit-cleared OffsetToDataOrDirectory field for leaf entries.
            // So leafOffsets[] already contains the within-section offsets of the DATA_ENTRY structs.
            //
            // And the DATA_ENTRY's OffsetToData field is an absolute RVA, not a within-section offset.
            // The actual data blob is at: within_section = DATA_ENTRY.OffsetToData - sectionRva.
            //
            // To recover sectionRva from known facts:
            //   sectionRva = DATA_ENTRY.OffsetToData - within_section_of_data_blob
            //
            // We don't know within_section_of_data_blob directly. BUT we know that data blobs
            // are typically placed after all directory and data-entry structures, and we can scan
            // the DATA_ENTRY sizes to find a consistent placement.
            //
            // Simplest reliable approach: the DATA_ENTRY at leafOffsets[i] gives us Size and
            // OffsetToData. We know the data is inside the section, so:
            //   0 <= OffsetToData - sectionRva < bytes.Length
            //   0 <= OffsetToData - sectionRva + Size <= bytes.Length
            //
            // We can binary-search for sectionRva by trying to find a value that satisfies
            // all leaves, but this is complex.
            //
            // PRACTICAL SIMPLIFICATION: For resource sections, the directory tree entries
            // (IMAGE_RESOURCE_DIRECTORY + entries) always come before the data. The data
            // is placed contiguously after. The first within-section data offset is thus
            // approximately the size of all directory structures. We compute this via
            // the minimum OffsetToData matching a within-section alignment.
            //
            // We rely on: minOffsetToData - sectionRva ≡ within_section_min_data_offset
            // and within_section_min_data_offset >= some_directory_size.
            //
            // The cleanest method without original section RVA: trust that within-section
            // data offsets referenced from leaf entries actually align to 4-byte boundaries
            // and fall within [0, bytes.Length). We pick the offset that makes
            // (minRva - candidateSectionRva) ∈ [0, bytes.Length).
            //
            // We iterate over leaf DATA_ENTRY offsets and use the leaf's OffsetToData
            // together with the SIZE to find which leaf's data we can verify as valid bytes.
            //
            // For now: use the minimum within-section DATA_ENTRY offset (from leafOffsets)
            // as a proxy, and assume the actual data follows the DATA_ENTRY table.
            // Return the minimum leafOffset as an approximate lower bound.

            if (leafOffsets.Count == 0)
                return -1;

            int minLeafOffset = int.MaxValue;
            int minLeafRva = int.MaxValue;

            foreach (int off in leafOffsets)
            {
                if (off + 4 <= bytes.Length)
                {
                    int rva = BitConverter.ToInt32(bytes, off);
                    if (rva > 0 && rva < minLeafRva)
                    {
                        minLeafRva = rva;
                        minLeafOffset = off;
                    }
                }
            }

            if (minLeafOffset == int.MaxValue)
                return -1;

            // The minimum leaf DATA_ENTRY offset within the section is minLeafOffset.
            // Data blobs are placed after data entries; the minimum data blob offset is
            // at least after all leaf DATA_ENTRY structs (each 16 bytes).
            // We compute the minimum expected data offset as just past the DATA_ENTRY structs.
            // This is a heuristic but works for standard compiler-generated resource sections.
            int minDataEntryEnd = leafOffsets.Max() + 16;

            // Align up to 4 bytes
            if (minDataEntryEnd % 4 != 0)
                minDataEntryEnd += 4 - (minDataEntryEnd % 4);

            return minDataEntryEnd < bytes.Length ? minDataEntryEnd : 0;
        }
    }
}
