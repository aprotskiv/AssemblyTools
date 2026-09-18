using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Runtime.Loader;
using LeXtudio.Metadata.Mutable;
using Xunit;

namespace LeXtudio.Metadata.Mutable.Tests;

/// <summary>
/// Verifies that MutableAssemblyWriter round-trips an assembly that contains a
/// foreach loop over a generic Dictionary without corrupting struct (Enumerator)
/// type metadata — the scenario that caused TypeLoadException in WpfDesign.Designer.
/// </summary>
public class MutableAssemblyWriterRoundTripTests
{
    [Fact]
    public void RoundTrip_ShortInlineArg_PreservesArgumentIndex()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "wxsg_arg_roundtrip_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var asmPath = Path.Combine(tempDir, "ArgumentRoundTrip.dll");
        var rewrittenPath = Path.Combine(tempDir, "ArgumentRoundTrip_rw.dll");

        try
        {
            EmitArgumentRoundTripAssembly(asmPath);

            var reader = new MutableAssemblyReader();
            var assembly = reader.Read(asmPath, new MutableReaderParameters { ReadMethodBodies = true });
            assembly.MainModule.FileName = rewrittenPath;

            var writer = new MutableAssemblyWriter(assembly);
            writer.Write(rewrittenPath);

            var rewritten = reader.Read(rewrittenPath, new MutableReaderParameters { ReadMethodBodies = true });
            var method = rewritten.MainModule.Types
                .Single(t => t.FullName == "RoundTripTest.ArgumentWriter")
                .Methods
                .Single(m => m.Name == "StoreSecondArgument");

            var starg = method.Body!.Instructions.Single(i => i.OpCode.Name == "starg.s");
            var operand = Assert.IsType<byte>(starg.Operand);
            Assert.Equal(2, operand);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void RoundTrip_DictionaryForeach_DoesNotCorruptEnumeratorStruct()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "wxsg_roundtrip_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var asmPath = Path.Combine(tempDir, "RoundTripTest.dll");
        var rewrittenPath = Path.Combine(tempDir, "RoundTripTest_rw.dll");

        try
        {
            EmitTestAssembly(asmPath);

            // Round-trip through MutableAssemblyReader / Writer (no changes)
            var bytes = File.ReadAllBytes(asmPath);
            var reader = new MutableAssemblyReader();
            var assembly = reader.Read(new MemoryStream(bytes), new MutableReaderParameters { ReadMethodBodies = true });
            assembly.MainModule.FileName = rewrittenPath;

            var writer = new MutableAssemblyWriter(assembly);
            writer.Write(rewrittenPath);

            // Load in an isolated context and invoke the method
            var ctx = new AssemblyLoadContext("RoundTripTest_" + Guid.NewGuid(), isCollectible: true);
            Assembly loaded;
            try
            {
                loaded = ctx.LoadFromAssemblyPath(rewrittenPath);
            }
            catch (Exception ex)
            {
                Assert.Fail($"Failed to load round-tripped assembly: {ex}");
                return;
            }

            var type = loaded.GetType("RoundTripTest.DictIterator");
            Assert.NotNull(type);
            var method = type!.GetMethod("IterateAndSum", BindingFlags.Public | BindingFlags.Static);
            Assert.NotNull(method);

            Exception? invokeEx = null;
            object? result = null;
            try
            {
                result = method!.Invoke(null, null);
            }
            catch (TargetInvocationException tie) { invokeEx = tie.InnerException ?? tie; }
            catch (Exception ex) { invokeEx = ex; }

            ctx.Unload();

            if (invokeEx != null)
                Assert.Fail($"Round-tripped assembly threw {invokeEx.GetType().Name}: {invokeEx.Message}\n{invokeEx}");

            Assert.Equal(6, result);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void RoundTrip_GenericInstanceFieldAccess_PreservesMemberReference()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "wxsg_generic_field_roundtrip_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var asmPath = Path.Combine(tempDir, "GenericFieldRoundTrip.dll");
        var rewrittenPath = Path.Combine(tempDir, "GenericFieldRoundTrip_rw.dll");

        try
        {
            EmitGenericInstanceFieldAssembly(asmPath);

            var reader = new MutableAssemblyReader();
            var assembly = reader.Read(asmPath, new MutableReaderParameters { ReadMethodBodies = true });

            var useBox = assembly.MainModule.Types.Single(t => t.FullName == "RoundTripTest.UseBox");
            var readValue = useBox.Methods.Single(m => m.Name == "ReadValue");
            var ldfld = readValue.Body!.Instructions.Single(i => i.OpCode.Name == "ldfld");
            var fieldRef = Assert.IsType<MutableFieldReference>(ldfld.Operand);
            Assert.False(fieldRef is MutableFieldDefinition, "Generic instance field access must remain a field reference, not collapse to a field definition.");
            Assert.IsType<MutableGenericInstanceType>(fieldRef.DeclaringType);

            assembly.MainModule.FileName = rewrittenPath;

            var writer = new MutableAssemblyWriter(assembly);
            writer.Write(rewrittenPath);

            var ctx = new AssemblyLoadContext("GenericFieldRoundTrip_" + Guid.NewGuid(), isCollectible: true);
            Assembly loaded;
            try
            {
                loaded = ctx.LoadFromAssemblyPath(rewrittenPath);
            }
            catch (Exception ex)
            {
                Assert.Fail($"Failed to load round-tripped assembly: {ex}");
                return;
            }

            var type = loaded.GetType("RoundTripTest.UseBox");
            Assert.NotNull(type);
            var method = type!.GetMethod("ReadValue", BindingFlags.Public | BindingFlags.Static);
            Assert.NotNull(method);
            Assert.Equal(42, method!.Invoke(null, null));

            ctx.Unload();
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void RoundTrip_GenericTypeStaticFieldAccess_RemainsInstantiable()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "wxsg_generic_static_field_roundtrip_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var asmPath = Path.Combine(tempDir, "GenericStaticFieldRoundTrip.dll");
        var rewrittenPath = Path.Combine(tempDir, "GenericStaticFieldRoundTrip_rw.dll");

        try
        {
            EmitGenericTypeStaticFieldAssembly(asmPath);

            var reader = new MutableAssemblyReader();
            var assembly = reader.Read(asmPath, new MutableReaderParameters { ReadMethodBodies = true });

            var sharedInstances = assembly.MainModule.Types.Single(t => t.FullName == "RoundTripTest.SharedInstances`1");
            var cctor = sharedInstances.Methods.Single(m => m.Name == ".cctor");
            var staticFieldOperands = cctor.Body!.Instructions
                .Where(i => i.OpCode.Name == "stsfld" || i.OpCode.Name == "ldsfld")
                .Select(i => Assert.IsType<MutableFieldReference>(i.Operand))
                .ToArray();
            Assert.All(staticFieldOperands, field =>
                Assert.IsType<MutableGenericInstanceType>(field.DeclaringType));

            assembly.MainModule.FileName = rewrittenPath;

            var writer = new MutableAssemblyWriter(assembly);
            writer.Write(rewrittenPath);

            var ctx = new AssemblyLoadContext("GenericStaticFieldRoundTrip_" + Guid.NewGuid(), isCollectible: true);
            var loaded = ctx.LoadFromAssemblyPath(rewrittenPath);
            var type = loaded.GetType("RoundTripTest.UseSharedInstances", throwOnError: true)!;
            var method = type.GetMethod("Run", BindingFlags.Public | BindingFlags.Static);
            Assert.NotNull(method);

            var exception = Record.Exception(() => method!.Invoke(null, null));
            ctx.Unload();

            Assert.Null(exception);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void RoundTrip_IndexerProperty_PreservesIndexedSignature()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "wxsg_indexer_property_roundtrip_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var asmPath = Path.Combine(tempDir, "IndexerPropertyRoundTrip.dll");
        var rewrittenPath = Path.Combine(tempDir, "IndexerPropertyRoundTrip_rw.dll");

        try
        {
            EmitIndexerPropertyAssembly(asmPath);

            var reader = new MutableAssemblyReader();
            var assembly = reader.Read(asmPath, new MutableReaderParameters { ReadMethodBodies = true });
            var property = assembly.MainModule.Types
                .Single(t => t.FullName == "RoundTripTest.IndexerHost")
                .Properties
                .Single(p => p.Name == "Item");
            Assert.Single(property.Parameters);
            Assert.Equal("System.Int32", property.Parameters[0].ParameterType.FullName);

            assembly.MainModule.FileName = rewrittenPath;

            var writer = new MutableAssemblyWriter(assembly);
            writer.Write(rewrittenPath);

            var rewritten = reader.Read(rewrittenPath, new MutableReaderParameters { ReadMethodBodies = true });
            property = rewritten.MainModule.Types
                .Single(t => t.FullName == "RoundTripTest.IndexerHost")
                .Properties
                .Single(p => p.Name == "Item");
            Assert.Single(property.Parameters);
            Assert.Equal("System.Int32", property.Parameters[0].ParameterType.FullName);

            var ctx = new AssemblyLoadContext("IndexerPropertyRoundTrip_" + Guid.NewGuid(), isCollectible: true);
            var loaded = ctx.LoadFromAssemblyPath(rewrittenPath);
            var type = loaded.GetType("RoundTripTest.IndexerHost", throwOnError: true)!;
            var reflectionProperty = type.GetProperty("Item", BindingFlags.Public | BindingFlags.Instance);
            Assert.NotNull(reflectionProperty);
            Assert.Single(reflectionProperty!.GetIndexParameters());

            var instance = Activator.CreateInstance(type);
            reflectionProperty.SetValue(instance, 27, new object[] { 1 });
            Assert.Equal(27, reflectionProperty.GetValue(instance, new object[] { 1 }));
            ctx.Unload();
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void RoundTrip_NestedGenericInstanceConstructor_PreservesMemberReference()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "wxsg_nested_generic_ctor_roundtrip_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var asmPath = Path.Combine(tempDir, "NestedGenericConstructorRoundTrip.dll");
        var rewrittenPath = Path.Combine(tempDir, "NestedGenericConstructorRoundTrip_rw.dll");

        try
        {
            EmitNestedGenericConstructorAssembly(asmPath);

            var reader = new MutableAssemblyReader();
            var assembly = reader.Read(asmPath, new MutableReaderParameters { ReadMethodBodies = true });

            var list = assembly.MainModule.Types.Single(t => t.FullName == "RoundTripTest.SingleItemList`1");
            var createEnumerator = list.Methods.Single(m => m.Name == "CreateEnumerator");
            var newobj = createEnumerator.Body!.Instructions.Single(i => i.OpCode.Name == "newobj");
            var ctorRef = Assert.IsType<MutableMethodReference>(newobj.Operand);
            Assert.False(ctorRef is MutableMethodDefinition, "Nested generic constructor access must remain a method reference, not collapse to a method definition.");
            Assert.IsType<MutableGenericInstanceType>(ctorRef.DeclaringType);

            assembly.MainModule.FileName = rewrittenPath;

            var writer = new MutableAssemblyWriter(assembly);
            writer.Write(rewrittenPath);

            var ctx = new AssemblyLoadContext("NestedGenericConstructorRoundTrip_" + Guid.NewGuid(), isCollectible: true);
            Assembly loaded;
            try
            {
                loaded = ctx.LoadFromAssemblyPath(rewrittenPath);
            }
            catch (Exception ex)
            {
                Assert.Fail($"Failed to load round-tripped assembly: {ex}");
                return;
            }

            var type = loaded.GetType("RoundTripTest.SingleItemList`1")!.MakeGenericType(typeof(int));
            var instance = Activator.CreateInstance(type, 42);
            var method = type.GetMethod("CreateEnumerator", BindingFlags.Public | BindingFlags.Instance);
            Assert.NotNull(method);
            var enumerator = method!.Invoke(instance, null);
            Assert.NotNull(enumerator);

            ctx.Unload();
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void RoundTrip_PinnedLocal_PreservesLocalSignature()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "wxsg_pinned_local_roundtrip_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var asmPath = Path.Combine(tempDir, "PinnedLocalRoundTrip.dll");
        var rewrittenPath = Path.Combine(tempDir, "PinnedLocalRoundTrip_rw.dll");

        try
        {
            EmitPinnedLocalAssembly(asmPath);

            var reader = new MutableAssemblyReader();
            var assembly = reader.Read(asmPath, new MutableReaderParameters { ReadMethodBodies = true });
            var method = assembly.MainModule.Types
                .Single(t => t.FullName == "RoundTripTest.PinnedLocal")
                .Methods
                .Single(m => m.Name == "PinReference");
            var pinnedLocal = method.Body!.Variables.Single(v => v.Index == 1);
            Assert.True(pinnedLocal.IsPinned);
            Assert.IsType<MutableByReferenceType>(pinnedLocal.VariableType);

            assembly.MainModule.FileName = rewrittenPath;

            var writer = new MutableAssemblyWriter(assembly);
            writer.Write(rewrittenPath);

            var rewritten = reader.Read(rewrittenPath, new MutableReaderParameters { ReadMethodBodies = true });
            method = rewritten.MainModule.Types
                .Single(t => t.FullName == "RoundTripTest.PinnedLocal")
                .Methods
                .Single(m => m.Name == "PinReference");
            pinnedLocal = method.Body!.Variables.Single(v => v.Index == 1);
            Assert.True(pinnedLocal.IsPinned);
            Assert.IsType<MutableByReferenceType>(pinnedLocal.VariableType);

            var ctx = new AssemblyLoadContext("PinnedLocalRoundTrip_" + Guid.NewGuid(), isCollectible: true);
            Assembly loaded;
            try
            {
                loaded = ctx.LoadFromAssemblyPath(rewrittenPath);
            }
            catch (Exception ex)
            {
                Assert.Fail($"Failed to load round-tripped assembly: {ex}");
                return;
            }

            var type = loaded.GetType("RoundTripTest.PinnedLocal");
            Assert.NotNull(type);
            method = null;
            var reflectionMethod = type!.GetMethod("PinReference", BindingFlags.Public | BindingFlags.Static);
            Assert.NotNull(reflectionMethod);
            var args = new object[] { 42 };
            Assert.Equal(42, reflectionMethod!.Invoke(null, args));

            ctx.Unload();
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Write_UnsignedAssembly_ClearsStrongNameSignedCorFlag()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "wxsg_strong_name_flag_roundtrip_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var asmPath = Path.Combine(tempDir, "StrongNameFlagRoundTrip.dll");
        var rewrittenPath = Path.Combine(tempDir, "StrongNameFlagRoundTrip_rw.dll");

        try
        {
            EmitArgumentRoundTripAssembly(asmPath);

            var reader = new MutableAssemblyReader();
            var assembly = reader.Read(asmPath, new MutableReaderParameters { ReadMethodBodies = true });
            assembly.MainModule.Attributes |= MutableModuleAttributes.StrongNameSigned;
            assembly.MainModule.FileName = rewrittenPath;

            var writer = new MutableAssemblyWriter(assembly);
            writer.Write(rewrittenPath);

            using var stream = File.OpenRead(rewrittenPath);
            using var peReader = new PEReader(stream);
            var corHeader = peReader.PEHeaders.CorHeader;
            Assert.NotNull(corHeader);
            Assert.False(corHeader!.Flags.HasFlag(CorFlags.StrongNameSigned));
            Assert.Equal(0, corHeader.StrongNameSignatureDirectory.Size);

            var ctx = new AssemblyLoadContext("StrongNameFlagRoundTrip_" + Guid.NewGuid(), isCollectible: true);
            var loaded = ctx.LoadFromAssemblyPath(rewrittenPath);
            Assert.NotNull(loaded);
            ctx.Unload();
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void RoundTrip_InitializedData_PreservesFieldRvaAndClassLayout()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "wxsg_field_rva_roundtrip_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var asmPath = Path.Combine(tempDir, "FieldRvaRoundTrip.dll");
        var rewrittenPath = Path.Combine(tempDir, "FieldRvaRoundTrip_rw.dll");

        try
        {
            EmitInitializedDataAssembly(asmPath);

            var reader = new MutableAssemblyReader();
            var assembly = reader.Read(asmPath, new MutableReaderParameters { ReadMethodBodies = true });
            assembly.MainModule.FileName = rewrittenPath;

            var writer = new MutableAssemblyWriter(assembly);
            writer.Write(rewrittenPath);

            using (var stream = File.OpenRead(rewrittenPath))
            using (var peReader = new PEReader(stream))
            {
                var metadata = peReader.GetMetadataReader();
                Assert.Equal(1, metadata.GetTableRowCount(TableIndex.FieldRva));

                var field = metadata.GetFieldDefinition(MetadataTokens.FieldDefinitionHandle(1));
                Assert.NotEqual(0, field.GetRelativeVirtualAddress());

                var arrayType = metadata.TypeDefinitions
                    .Select(metadata.GetTypeDefinition)
                    .Single(t => metadata.GetString(t.Name) == "$ArrayType$4");
                Assert.Equal(4, arrayType.GetLayout().Size);

                var data = peReader.GetSectionData(field.GetRelativeVirtualAddress()).GetReader(0, 4).ReadBytes(4);
                Assert.Equal(new byte[] { 1, 2, 3, 4 }, data);
            }

            var ctx = new AssemblyLoadContext("FieldRvaRoundTrip_" + Guid.NewGuid(), isCollectible: true);
            var loaded = ctx.LoadFromAssemblyPath(rewrittenPath);
            var loadException = Record.Exception(() => loaded.GetTypes());
            ctx.Unload();

            Assert.Null(loadException);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void RoundTrip_CustomAttributeSystemTypeValue_PreservesAssemblyQualifiedName()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "wxsg_custom_attr_type_roundtrip_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var dependencyPath = Path.Combine(tempDir, "CustomAttributeTypeDependency.dll");
        var asmPath = Path.Combine(tempDir, "CustomAttributeTypeRoundTrip.dll");
        var rewrittenPath = Path.Combine(tempDir, "CustomAttributeTypeRoundTrip_rw.dll");

        try
        {
            EmitExternalTypeAssembly(dependencyPath);
            var dependency = AssemblyLoadContext.Default.LoadFromAssemblyPath(dependencyPath);
            var externalType = dependency.GetType("ExternalNamespace.ExternalType", throwOnError: true)!;
            EmitCustomAttributeTypeValueAssembly(asmPath, externalType);

            var reader = new MutableAssemblyReader();
            var assembly = reader.Read(asmPath, new MutableReaderParameters { ReadMethodBodies = true });
            assembly.MainModule.FileName = rewrittenPath;

            var writer = new MutableAssemblyWriter(assembly);
            writer.Write(rewrittenPath);

            using (var stream = File.OpenRead(rewrittenPath))
            using (var peReader = new PEReader(stream))
            {
                var metadata = peReader.GetMetadataReader();
                var matchingAttribute = metadata.CustomAttributes
                    .Select(metadata.GetCustomAttribute)
                    .Select(attribute => metadata.GetBlobBytes(attribute.Value))
                    .Select(bytes => System.Text.Encoding.UTF8.GetString(bytes))
                    .Single(value => value.Contains("ExternalNamespace.ExternalType", StringComparison.Ordinal));

                Assert.Contains("CustomAttributeTypeDependency", matchingAttribute);
            }

            var ctx = new AssemblyLoadContext("CustomAttributeTypeRoundTrip_" + Guid.NewGuid(), isCollectible: true);
            ctx.Resolving += (_, name) =>
                name.Name == "CustomAttributeTypeDependency"
                    ? ctx.LoadFromAssemblyPath(dependencyPath)
                    : null;

            var loaded = ctx.LoadFromAssemblyPath(rewrittenPath);
            var targetType = loaded.GetType("RoundTripTest.Target", throwOnError: true)!;
            var typeArgument = targetType.GetCustomAttributesData()
                .Single(attribute => attribute.AttributeType.Name == "TypeMetadataAttribute")
                .ConstructorArguments
                .Single()
                .Value;

            Assert.Equal("ExternalNamespace.ExternalType", ((Type)typeArgument!).FullName);
            ctx.Unload();
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void RoundTrip_CustomAttributeExternalEnumConstructorArguments_PreservesValues()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "wxsg_custom_attr_enum_roundtrip_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var dependencyPath = Path.Combine(tempDir, "CustomAttributeEnumDependency.dll");
        var asmPath = Path.Combine(tempDir, "CustomAttributeEnumRoundTrip.dll");
        var rewrittenPath = Path.Combine(tempDir, "CustomAttributeEnumRoundTrip_rw.dll");

        try
        {
            EmitExternalEnumAttributeDependency(dependencyPath);
            var dependency = AssemblyLoadContext.Default.LoadFromAssemblyPath(dependencyPath);
            var enumType = dependency.GetType("ExternalNamespace.ThemeLocation", throwOnError: true)!;
            var attributeType = dependency.GetType("ExternalNamespace.ThemeLikeAttribute", throwOnError: true)!;
            EmitCustomAttributeExternalEnumAssembly(asmPath, attributeType, enumType);

            var reader = new MutableAssemblyReader();
            var assembly = reader.Read(asmPath, new MutableReaderParameters { ReadMethodBodies = true });
            assembly.MainModule.FileName = rewrittenPath;

            var writer = new MutableAssemblyWriter(assembly);
            writer.Write(rewrittenPath);

            var ctx = new AssemblyLoadContext("CustomAttributeEnumRoundTrip_" + Guid.NewGuid(), isCollectible: true);
            ctx.Resolving += (_, name) =>
                name.Name == "CustomAttributeEnumDependency"
                    ? ctx.LoadFromAssemblyPath(dependencyPath)
                    : null;

            var loaded = ctx.LoadFromAssemblyPath(rewrittenPath);
            var targetType = loaded.GetType("RoundTripTest.Target", throwOnError: true)!;
            var attribute = targetType.GetCustomAttributesData()
                .Single(attr => attr.AttributeType.FullName == "ExternalNamespace.ThemeLikeAttribute");

            Assert.Equal(0, Convert.ToInt32(attribute.ConstructorArguments[0].Value));
            Assert.Equal(1, Convert.ToInt32(attribute.ConstructorArguments[1].Value));
            ctx.Unload();
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void RoundTrip_CustomAttributeNamedTypeArrayValue_RemainsReflectable()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "wxsg_custom_attr_type_array_roundtrip_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var asmPath = Path.Combine(tempDir, "CustomAttributeTypeArrayRoundTrip.dll");
        var rewrittenPath = Path.Combine(tempDir, "CustomAttributeTypeArrayRoundTrip_rw.dll");

        try
        {
            EmitCustomAttributeNamedTypeArrayValueAssembly(asmPath);

            var reader = new MutableAssemblyReader();
            var assembly = reader.Read(asmPath, new MutableReaderParameters { ReadMethodBodies = true });
            assembly.MainModule.FileName = rewrittenPath;

            var writer = new MutableAssemblyWriter(assembly);
            writer.Write(rewrittenPath);

            var ctx = new AssemblyLoadContext("CustomAttributeTypeArrayRoundTrip_" + Guid.NewGuid(), isCollectible: true);
            var loaded = ctx.LoadFromAssemblyPath(rewrittenPath);
            var targetType = loaded.GetType("RoundTripTest.Target", throwOnError: true)!;
            var attribute = targetType.GetCustomAttributes(inherit: false)
                .Single(a => a.GetType().Name == "TypeArrayMetadataAttribute");
            var overrides = Assert.IsType<Type[]>(attribute.GetType().GetProperty("OverrideExtensions")!.GetValue(attribute));

            Assert.Equal(new[] { "System.String", "System.Int32" }, overrides.Select(t => t.FullName).ToArray());
            ctx.Unload();
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void RoundTrip_CustomAttributeNamedTypeValue_RemainsReflectable()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "wxsg_custom_attr_named_type_roundtrip_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var asmPath = Path.Combine(tempDir, "CustomAttributeNamedTypeRoundTrip.dll");
        var rewrittenPath = Path.Combine(tempDir, "CustomAttributeNamedTypeRoundTrip_rw.dll");

        try
        {
            EmitCustomAttributeNamedTypeValueAssembly(asmPath);

            var reader = new MutableAssemblyReader();
            var assembly = reader.Read(asmPath, new MutableReaderParameters { ReadMethodBodies = true });
            assembly.MainModule.FileName = rewrittenPath;

            var writer = new MutableAssemblyWriter(assembly);
            writer.Write(rewrittenPath);

            var ctx = new AssemblyLoadContext("CustomAttributeNamedTypeRoundTrip_" + Guid.NewGuid(), isCollectible: true);
            var loaded = ctx.LoadFromAssemblyPath(rewrittenPath);
            var targetType = loaded.GetType("RoundTripTest.Target", throwOnError: true)!;
            var attribute = targetType.GetCustomAttributes(inherit: false)
                .Single(a => a.GetType().Name == "NamedTypeMetadataAttribute");
            var overrideType = Assert.IsAssignableFrom<Type>(attribute.GetType().GetProperty("OverrideExtension")!.GetValue(attribute));

            Assert.Equal(typeof(string).FullName, overrideType.FullName);
            ctx.Unload();
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void RoundTrip_CustomAttributeGenericTypeValue_RemainsReflectable()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "wxsg_custom_attr_generic_type_roundtrip_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var asmPath = Path.Combine(tempDir, "CustomAttributeGenericTypeRoundTrip.dll");
        var rewrittenPath = Path.Combine(tempDir, "CustomAttributeGenericTypeRoundTrip_rw.dll");

        try
        {
            EmitCustomAttributeGenericTypeValueAssembly(asmPath);

            var reader = new MutableAssemblyReader();
            var assembly = reader.Read(asmPath, new MutableReaderParameters { ReadMethodBodies = true });
            assembly.MainModule.FileName = rewrittenPath;

            var writer = new MutableAssemblyWriter(assembly);
            writer.Write(rewrittenPath);

            var ctx = new AssemblyLoadContext("CustomAttributeGenericTypeRoundTrip_" + Guid.NewGuid(), isCollectible: true);
            var loaded = ctx.LoadFromAssemblyPath(rewrittenPath);
            var targetType = loaded.GetType("RoundTripTest.Target", throwOnError: true)!;
            var attribute = targetType.GetCustomAttributes(inherit: false)
                .Single(a => a.GetType().Name == "GenericTypeMetadataAttribute");
            var extensionServerType = Assert.IsAssignableFrom<Type>(
                attribute.GetType().GetProperty("ExtensionServerType")!.GetValue(attribute));

            Assert.True(extensionServerType.IsConstructedGenericType);
            Assert.Equal("RoundTripTest.LogicalOrServer`2", extensionServerType.GetGenericTypeDefinition().FullName);
            Assert.Equal(
                new[] { "RoundTripTest.PrimaryServer", "RoundTripTest.ParentServer" },
                extensionServerType.GetGenericArguments().Select(t => t.FullName).ToArray());
            ctx.Unload();
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    private static void EmitArgumentRoundTripAssembly(string outputPath)
    {
        var asmName = new AssemblyName("ArgumentRoundTrip");
        var asmBuilder = new PersistedAssemblyBuilder(asmName, typeof(object).Assembly);
        var modBuilder = asmBuilder.DefineDynamicModule("ArgumentRoundTrip");
        var typeBuilder = modBuilder.DefineType("RoundTripTest.ArgumentWriter",
            TypeAttributes.Public | TypeAttributes.Class);

        var methodBuilder = typeBuilder.DefineMethod("StoreSecondArgument",
            MethodAttributes.Public,
            typeof(object),
            new[] { typeof(object), typeof(object) });

        var il = methodBuilder.GetILGenerator();
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Starg_S, (short)2);
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Ret);

        typeBuilder.CreateType();
        asmBuilder.Save(outputPath);
    }

    private static void EmitExternalTypeAssembly(string outputPath)
    {
        var asmName = new AssemblyName("CustomAttributeTypeDependency");
        var asmBuilder = new PersistedAssemblyBuilder(asmName, typeof(object).Assembly);
        var modBuilder = asmBuilder.DefineDynamicModule("CustomAttributeTypeDependency");
        var typeBuilder = modBuilder.DefineType(
            "ExternalNamespace.ExternalType",
            TypeAttributes.Public | TypeAttributes.Class);

        typeBuilder.CreateType();
        asmBuilder.Save(outputPath);
    }

    private static void EmitExternalEnumAttributeDependency(string outputPath)
    {
        var asmName = new AssemblyName("CustomAttributeEnumDependency");
        var asmBuilder = new PersistedAssemblyBuilder(asmName, typeof(object).Assembly);
        var modBuilder = asmBuilder.DefineDynamicModule("CustomAttributeEnumDependency");

        var enumBuilder = modBuilder.DefineEnum(
            "ExternalNamespace.ThemeLocation",
            TypeAttributes.Public,
            typeof(int));
        enumBuilder.DefineLiteral("None", 0);
        enumBuilder.DefineLiteral("SourceAssembly", 1);
        var enumType = enumBuilder.CreateType();

        var attributeBuilder = modBuilder.DefineType(
            "ExternalNamespace.ThemeLikeAttribute",
            TypeAttributes.Public | TypeAttributes.Class | TypeAttributes.Sealed,
            typeof(Attribute));
        var constructor = attributeBuilder.DefineConstructor(
            MethodAttributes.Public,
            CallingConventions.Standard,
            new[] { enumType, enumType });
        var il = constructor.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, typeof(Attribute).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            Type.EmptyTypes,
            modifiers: null)!);
        il.Emit(OpCodes.Ret);
        attributeBuilder.CreateType();

        asmBuilder.Save(outputPath);
    }

    private static void EmitGenericTypeStaticFieldAssembly(string outputPath)
    {
        var asmName = new AssemblyName("GenericStaticFieldRoundTrip");
        var asmBuilder = new PersistedAssemblyBuilder(asmName, typeof(object).Assembly);
        var modBuilder = asmBuilder.DefineDynamicModule("GenericStaticFieldRoundTrip");

        var enumBuilder = modBuilder.DefineEnum(
            "RoundTripTest.TestEnum",
            TypeAttributes.Public,
            typeof(int));
        enumBuilder.DefineLiteral("One", 1);
        var enumType = enumBuilder.CreateType();

        var sharedBuilder = modBuilder.DefineType(
            "RoundTripTest.SharedInstances`1",
            TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);
        var genericParameter = sharedBuilder.DefineGenericParameters("T")[0];
        genericParameter.SetGenericParameterAttributes(GenericParameterAttributes.NotNullableValueTypeConstraint);

        var dictionaryType = typeof(Dictionary<,>).MakeGenericType(genericParameter, typeof(object));
        var field = sharedBuilder.DefineField(
            "_boxedEnumValues",
            dictionaryType,
            FieldAttributes.Private | FieldAttributes.Static);

        var cctor = sharedBuilder.DefineConstructor(
            MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName,
            CallingConventions.Standard,
            Type.EmptyTypes);
        var il = cctor.GetILGenerator();
        il.Emit(OpCodes.Newobj, TypeBuilder.GetConstructor(
            dictionaryType,
            typeof(Dictionary<,>).GetConstructor(Type.EmptyTypes)!));
        il.Emit(OpCodes.Stsfld, field);
        il.Emit(OpCodes.Ret);

        var hasValue = sharedBuilder.DefineMethod(
            "HasValue",
            MethodAttributes.Public | MethodAttributes.Static,
            typeof(bool),
            new[] { genericParameter });
        il = hasValue.GetILGenerator();
        il.Emit(OpCodes.Ldsfld, field);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Callvirt, TypeBuilder.GetMethod(
            dictionaryType,
            typeof(Dictionary<,>).GetMethod("ContainsKey")!));
        il.Emit(OpCodes.Ret);
        var sharedType = sharedBuilder.CreateType();

        var useBuilder = modBuilder.DefineType(
            "RoundTripTest.UseSharedInstances",
            TypeAttributes.Public | TypeAttributes.Class);
        var run = useBuilder.DefineMethod(
            "Run",
            MethodAttributes.Public | MethodAttributes.Static,
            typeof(bool),
            Type.EmptyTypes);
        il = run.GetILGenerator();
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Call, TypeBuilder.GetMethod(sharedType.MakeGenericType(enumType), hasValue));
        il.Emit(OpCodes.Ret);
        useBuilder.CreateType();

        asmBuilder.Save(outputPath);
    }

    private static void EmitIndexerPropertyAssembly(string outputPath)
    {
        var asmName = new AssemblyName("IndexerPropertyRoundTrip");
        var asmBuilder = new PersistedAssemblyBuilder(asmName, typeof(object).Assembly);
        var modBuilder = asmBuilder.DefineDynamicModule("IndexerPropertyRoundTrip");

        var typeBuilder = modBuilder.DefineType(
            "RoundTripTest.IndexerHost",
            TypeAttributes.Public | TypeAttributes.Class);

        var backingField = typeBuilder.DefineField(
            "_values",
            typeof(int[]),
            FieldAttributes.Private | FieldAttributes.InitOnly);

        var constructor = typeBuilder.DefineConstructor(
            MethodAttributes.Public,
            CallingConventions.Standard,
            Type.EmptyTypes);
        var il = constructor.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, typeof(object).GetConstructor(Type.EmptyTypes)!);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldc_I4_3);
        il.Emit(OpCodes.Newarr, typeof(int));
        il.Emit(OpCodes.Stfld, backingField);
        il.Emit(OpCodes.Ret);

        var getter = typeBuilder.DefineMethod(
            "get_Item",
            MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.HideBySig,
            typeof(int),
            new[] { typeof(int) });
        il = getter.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, backingField);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldelem_I4);
        il.Emit(OpCodes.Ret);

        var setter = typeBuilder.DefineMethod(
            "set_Item",
            MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.HideBySig,
            typeof(void),
            new[] { typeof(int), typeof(int) });
        il = setter.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, backingField);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Stelem_I4);
        il.Emit(OpCodes.Ret);

        var property = typeBuilder.DefineProperty(
            "Item",
            PropertyAttributes.None,
            CallingConventions.HasThis,
            typeof(int),
            new[] { typeof(int) });
        property.SetGetMethod(getter);
        property.SetSetMethod(setter);

        var defaultMemberConstructor = typeof(System.Reflection.DefaultMemberAttribute)
            .GetConstructor(new[] { typeof(string) })!;
        typeBuilder.SetCustomAttribute(new CustomAttributeBuilder(defaultMemberConstructor, new object[] { "Item" }));

        typeBuilder.CreateType();
        asmBuilder.Save(outputPath);
    }

    private static void EmitCustomAttributeExternalEnumAssembly(string outputPath, Type attributeType, Type enumType)
    {
        var asmName = new AssemblyName("CustomAttributeEnumRoundTrip");
        var asmBuilder = new PersistedAssemblyBuilder(asmName, typeof(object).Assembly);
        var modBuilder = asmBuilder.DefineDynamicModule("CustomAttributeEnumRoundTrip");

        var targetBuilder = modBuilder.DefineType(
            "RoundTripTest.Target",
            TypeAttributes.Public | TypeAttributes.Class);
        targetBuilder.SetCustomAttribute(new CustomAttributeBuilder(
            attributeType.GetConstructor(new[] { enumType, enumType })!,
            new[] { Enum.ToObject(enumType, 0), Enum.ToObject(enumType, 1) }));
        targetBuilder.CreateType();

        asmBuilder.Save(outputPath);
    }

    private static void EmitCustomAttributeTypeValueAssembly(string outputPath, Type externalType)
    {
        var asmName = new AssemblyName("CustomAttributeTypeRoundTrip");
        var asmBuilder = new PersistedAssemblyBuilder(asmName, typeof(object).Assembly);
        var modBuilder = asmBuilder.DefineDynamicModule("CustomAttributeTypeRoundTrip");

        var attributeBuilder = modBuilder.DefineType(
            "RoundTripTest.TypeMetadataAttribute",
            TypeAttributes.Public | TypeAttributes.Class | TypeAttributes.Sealed,
            typeof(Attribute));
        var constructor = attributeBuilder.DefineConstructor(
            MethodAttributes.Public,
            CallingConventions.Standard,
            new[] { typeof(Type) });
        var il = constructor.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, typeof(Attribute).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            Type.EmptyTypes,
            modifiers: null)!);
        il.Emit(OpCodes.Ret);

        attributeBuilder.CreateType();

        var targetBuilder = modBuilder.DefineType(
            "RoundTripTest.Target",
            TypeAttributes.Public | TypeAttributes.Class);
        targetBuilder.SetCustomAttribute(new CustomAttributeBuilder(constructor, new object[] { externalType }));
        targetBuilder.CreateType();

        asmBuilder.Save(outputPath);
    }

    private static void EmitCustomAttributeNamedTypeArrayValueAssembly(string outputPath)
    {
        var asmName = new AssemblyName("CustomAttributeTypeArrayRoundTrip");
        var asmBuilder = new PersistedAssemblyBuilder(asmName, typeof(object).Assembly);
        var modBuilder = asmBuilder.DefineDynamicModule("CustomAttributeTypeArrayRoundTrip");

        var attributeBuilder = modBuilder.DefineType(
            "RoundTripTest.TypeArrayMetadataAttribute",
            TypeAttributes.Public | TypeAttributes.Class | TypeAttributes.Sealed,
            typeof(Attribute));
        var constructor = attributeBuilder.DefineConstructor(
            MethodAttributes.Public,
            CallingConventions.Standard,
            Type.EmptyTypes);
        var il = constructor.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, typeof(Attribute).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            Type.EmptyTypes,
            modifiers: null)!);
        il.Emit(OpCodes.Ret);

        var backingField = attributeBuilder.DefineField(
            "_overrideExtensions",
            typeof(Type[]),
            FieldAttributes.Private);
        var property = attributeBuilder.DefineProperty(
            "OverrideExtensions",
            PropertyAttributes.None,
            typeof(Type[]),
            Type.EmptyTypes);
        var getter = attributeBuilder.DefineMethod(
            "get_OverrideExtensions",
            MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.HideBySig,
            typeof(Type[]),
            Type.EmptyTypes);
        il = getter.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, backingField);
        il.Emit(OpCodes.Ret);
        var setter = attributeBuilder.DefineMethod(
            "set_OverrideExtensions",
            MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.HideBySig,
            null,
            new[] { typeof(Type[]) });
        il = setter.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Stfld, backingField);
        il.Emit(OpCodes.Ret);
        property.SetGetMethod(getter);
        property.SetSetMethod(setter);

        attributeBuilder.CreateType();

        var targetBuilder = modBuilder.DefineType(
            "RoundTripTest.Target",
            TypeAttributes.Public | TypeAttributes.Class);
        targetBuilder.SetCustomAttribute(new CustomAttributeBuilder(
            constructor,
            Array.Empty<object>(),
            new[] { property },
            new object[] { new[] { typeof(string), typeof(int) } }));
        targetBuilder.CreateType();

        asmBuilder.Save(outputPath);
    }

    private static void EmitCustomAttributeNamedTypeValueAssembly(string outputPath)
    {
        var asmName = new AssemblyName("CustomAttributeNamedTypeRoundTrip");
        var asmBuilder = new PersistedAssemblyBuilder(asmName, typeof(object).Assembly);
        var modBuilder = asmBuilder.DefineDynamicModule("CustomAttributeNamedTypeRoundTrip");

        var attributeBuilder = modBuilder.DefineType(
            "RoundTripTest.NamedTypeMetadataAttribute",
            TypeAttributes.Public | TypeAttributes.Class | TypeAttributes.Sealed,
            typeof(Attribute));
        var constructor = attributeBuilder.DefineConstructor(
            MethodAttributes.Public,
            CallingConventions.Standard,
            Type.EmptyTypes);
        var il = constructor.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, typeof(Attribute).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            Type.EmptyTypes,
            modifiers: null)!);
        il.Emit(OpCodes.Ret);

        var backingField = attributeBuilder.DefineField(
            "_overrideExtension",
            typeof(Type),
            FieldAttributes.Private);
        var property = attributeBuilder.DefineProperty(
            "OverrideExtension",
            PropertyAttributes.None,
            typeof(Type),
            Type.EmptyTypes);
        var getter = attributeBuilder.DefineMethod(
            "get_OverrideExtension",
            MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.HideBySig,
            typeof(Type),
            Type.EmptyTypes);
        il = getter.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, backingField);
        il.Emit(OpCodes.Ret);
        var setter = attributeBuilder.DefineMethod(
            "set_OverrideExtension",
            MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.HideBySig,
            null,
            new[] { typeof(Type) });
        il = setter.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Stfld, backingField);
        il.Emit(OpCodes.Ret);
        property.SetGetMethod(getter);
        property.SetSetMethod(setter);

        attributeBuilder.CreateType();

        var targetBuilder = modBuilder.DefineType(
            "RoundTripTest.Target",
            TypeAttributes.Public | TypeAttributes.Class);
        targetBuilder.SetCustomAttribute(new CustomAttributeBuilder(
            constructor,
            Array.Empty<object>(),
            new[] { property },
            new object[] { typeof(string) }));
        targetBuilder.CreateType();

        asmBuilder.Save(outputPath);
    }

    private static void EmitCustomAttributeGenericTypeValueAssembly(string outputPath)
    {
        var asmName = new AssemblyName("CustomAttributeGenericTypeRoundTrip");
        var asmBuilder = new PersistedAssemblyBuilder(asmName, typeof(object).Assembly);
        var modBuilder = asmBuilder.DefineDynamicModule("CustomAttributeGenericTypeRoundTrip");

        var primaryServer = modBuilder.DefineType(
            "RoundTripTest.PrimaryServer",
            TypeAttributes.Public | TypeAttributes.Class).CreateType();
        var parentServer = modBuilder.DefineType(
            "RoundTripTest.ParentServer",
            TypeAttributes.Public | TypeAttributes.Class).CreateType();
        var logicalOrServerBuilder = modBuilder.DefineType(
            "RoundTripTest.LogicalOrServer`2",
            TypeAttributes.Public | TypeAttributes.Class);
        logicalOrServerBuilder.DefineGenericParameters("TPrimary", "TParent");
        var logicalOrServer = logicalOrServerBuilder.CreateType();

        var attributeBuilder = modBuilder.DefineType(
            "RoundTripTest.GenericTypeMetadataAttribute",
            TypeAttributes.Public | TypeAttributes.Class | TypeAttributes.Sealed,
            typeof(Attribute));
        var backingField = attributeBuilder.DefineField(
            "_extensionServerType",
            typeof(Type),
            FieldAttributes.Private);
        var constructor = attributeBuilder.DefineConstructor(
            MethodAttributes.Public,
            CallingConventions.Standard,
            new[] { typeof(Type) });
        var il = constructor.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, typeof(Attribute).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            Type.EmptyTypes,
            modifiers: null)!);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Stfld, backingField);
        il.Emit(OpCodes.Ret);
        var property = attributeBuilder.DefineProperty(
            "ExtensionServerType",
            PropertyAttributes.None,
            typeof(Type),
            Type.EmptyTypes);
        var getter = attributeBuilder.DefineMethod(
            "get_ExtensionServerType",
            MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.HideBySig,
            typeof(Type),
            Type.EmptyTypes);
        il = getter.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, backingField);
        il.Emit(OpCodes.Ret);
        property.SetGetMethod(getter);
        attributeBuilder.CreateType();

        var targetBuilder = modBuilder.DefineType(
            "RoundTripTest.Target",
            TypeAttributes.Public | TypeAttributes.Class);
        targetBuilder.SetCustomAttribute(new CustomAttributeBuilder(
            constructor,
            new object[] { logicalOrServer.MakeGenericType(primaryServer, parentServer) }));
        targetBuilder.CreateType();

        asmBuilder.Save(outputPath);
    }

    private static void EmitInitializedDataAssembly(string outputPath)
    {
        var asmName = new AssemblyName("FieldRvaRoundTrip");
        var asmBuilder = new PersistedAssemblyBuilder(asmName, typeof(object).Assembly);
        var modBuilder = asmBuilder.DefineDynamicModule("FieldRvaRoundTrip");

        modBuilder.DefineInitializedData(
            "Blob",
            new byte[] { 1, 2, 3, 4 },
            FieldAttributes.Assembly | FieldAttributes.Static | FieldAttributes.InitOnly);

        asmBuilder.Save(outputPath);
    }

    private static void EmitTestAssembly(string outputPath)
    {
        var asmName = new AssemblyName("RoundTripTest");
        // PersistedAssemblyBuilder is the .NET 9+ save-capable builder
        var asmBuilder = new PersistedAssemblyBuilder(asmName, typeof(object).Assembly);
        var modBuilder = asmBuilder.DefineDynamicModule("RoundTripTest");
        var typeBuilder = modBuilder.DefineType("RoundTripTest.DictIterator",
            TypeAttributes.Public | TypeAttributes.Class | TypeAttributes.Sealed | TypeAttributes.Abstract);

        // public static int IterateAndSum() — does foreach over Dictionary<string,int>
        var methodBuilder = typeBuilder.DefineMethod("IterateAndSum",
            MethodAttributes.Public | MethodAttributes.Static, typeof(int), Type.EmptyTypes);

        var il = methodBuilder.GetILGenerator();
        var dictType = typeof(Dictionary<string, int>);
        var kvpType = typeof(KeyValuePair<string, int>);
        // Dictionary<string,int>.Enumerator — the value type whose struct layout must survive round-trip
        var enumType = dictType.GetNestedType("Enumerator")!.MakeGenericType(typeof(string), typeof(int));

        var dictLocal = il.DeclareLocal(dictType);
        var sumLocal  = il.DeclareLocal(typeof(int));
        var enumLocal = il.DeclareLocal(enumType);
        var pairLocal = il.DeclareLocal(kvpType);

        il.Emit(OpCodes.Newobj, dictType.GetConstructor(Type.EmptyTypes)!);
        il.Emit(OpCodes.Stloc, dictLocal);

        foreach (var (key, val) in new[] { ("a", 1), ("b", 2), ("c", 3) })
        {
            il.Emit(OpCodes.Ldloc, dictLocal);
            il.Emit(OpCodes.Ldstr, key);
            il.Emit(OpCodes.Ldc_I4, val);
            il.Emit(OpCodes.Callvirt, dictType.GetMethod("set_Item")!);
        }

        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Stloc, sumLocal);

        il.Emit(OpCodes.Ldloc, dictLocal);
        il.Emit(OpCodes.Callvirt, dictType.GetMethod("GetEnumerator")!);
        il.Emit(OpCodes.Stloc, enumLocal);

        var loopStart = il.DefineLabel();
        var loopEnd   = il.DefineLabel();

        il.MarkLabel(loopStart);
        il.Emit(OpCodes.Ldloca, enumLocal);
        il.Emit(OpCodes.Call, enumType.GetMethod("MoveNext")!);
        il.Emit(OpCodes.Brfalse, loopEnd);

        il.Emit(OpCodes.Ldloca, enumLocal);
        il.Emit(OpCodes.Call, enumType.GetProperty("Current")!.GetGetMethod()!);
        il.Emit(OpCodes.Stloc, pairLocal);

        il.Emit(OpCodes.Ldloc, sumLocal);
        il.Emit(OpCodes.Ldloca, pairLocal);
        il.Emit(OpCodes.Call, kvpType.GetProperty("Value")!.GetGetMethod()!);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stloc, sumLocal);

        il.Emit(OpCodes.Br, loopStart);
        il.MarkLabel(loopEnd);

        il.Emit(OpCodes.Ldloc, sumLocal);
        il.Emit(OpCodes.Ret);

        typeBuilder.CreateType();
        asmBuilder.Save(outputPath);
    }

    private static void EmitGenericInstanceFieldAssembly(string outputPath)
    {
        var asmName = new AssemblyName("GenericFieldRoundTrip");
        var asmBuilder = new PersistedAssemblyBuilder(asmName, typeof(object).Assembly);
        var modBuilder = asmBuilder.DefineDynamicModule("GenericFieldRoundTrip");

        var boxBuilder = modBuilder.DefineType("RoundTripTest.Box`1",
            TypeAttributes.Public | TypeAttributes.Class);
        var typeParameter = boxBuilder.DefineGenericParameters("T")[0];
        var valueField = boxBuilder.DefineField("Value", typeParameter, FieldAttributes.Public);

        var boxCtor = boxBuilder.DefineConstructor(MethodAttributes.Public, CallingConventions.HasThis, Type.EmptyTypes);
        var il = boxCtor.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, typeof(object).GetConstructor(Type.EmptyTypes)!);
        il.Emit(OpCodes.Ret);

        var useBoxBuilder = modBuilder.DefineType("RoundTripTest.UseBox",
            TypeAttributes.Public | TypeAttributes.Class | TypeAttributes.Sealed | TypeAttributes.Abstract);
        var readValue = useBoxBuilder.DefineMethod("ReadValue",
            MethodAttributes.Public | MethodAttributes.Static,
            typeof(int),
            Type.EmptyTypes);

        var boxOfInt = boxBuilder.MakeGenericType(typeof(int));
        var valueOnBoxOfInt = TypeBuilder.GetField(boxOfInt, valueField);
        var boxCtorOnBoxOfInt = TypeBuilder.GetConstructor(boxOfInt, boxCtor);
        il = readValue.GetILGenerator();
        var boxLocal = il.DeclareLocal(boxOfInt);
        il.Emit(OpCodes.Newobj, boxCtorOnBoxOfInt);
        il.Emit(OpCodes.Stloc, boxLocal);
        il.Emit(OpCodes.Ldloc, boxLocal);
        il.Emit(OpCodes.Ldc_I4, 42);
        il.Emit(OpCodes.Stfld, valueOnBoxOfInt);
        il.Emit(OpCodes.Ldloc, boxLocal);
        il.Emit(OpCodes.Ldfld, valueOnBoxOfInt);
        il.Emit(OpCodes.Ret);

        boxBuilder.CreateType();
        useBoxBuilder.CreateType();
        asmBuilder.Save(outputPath);
    }

    private static void EmitNestedGenericConstructorAssembly(string outputPath)
    {
        var asmName = new AssemblyName("NestedGenericConstructorRoundTrip");
        var asmBuilder = new PersistedAssemblyBuilder(asmName, typeof(object).Assembly);
        var modBuilder = asmBuilder.DefineDynamicModule("NestedGenericConstructorRoundTrip");

        var listBuilder = modBuilder.DefineType("RoundTripTest.SingleItemList`1",
            TypeAttributes.Public | TypeAttributes.Class);
        var listTypeParameter = listBuilder.DefineGenericParameters("T")[0];
        var itemField = listBuilder.DefineField("_item", listTypeParameter, FieldAttributes.Private | FieldAttributes.InitOnly);

        var ctor = listBuilder.DefineConstructor(MethodAttributes.Public, CallingConventions.HasThis, new[] { listTypeParameter });
        var il = ctor.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, typeof(object).GetConstructor(Type.EmptyTypes)!);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Stfld, itemField);
        il.Emit(OpCodes.Ret);

        var enumeratorBuilder = listBuilder.DefineNestedType("Enumerator`1",
            TypeAttributes.NestedPublic | TypeAttributes.Class);
        var enumeratorTypeParameter = enumeratorBuilder.DefineGenericParameters("T")[0];
        var enumeratorItemField = enumeratorBuilder.DefineField("_item", enumeratorTypeParameter, FieldAttributes.Private | FieldAttributes.InitOnly);
        var enumeratorCtor = enumeratorBuilder.DefineConstructor(MethodAttributes.Public, CallingConventions.HasThis, new[] { enumeratorTypeParameter });
        il = enumeratorCtor.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, typeof(object).GetConstructor(Type.EmptyTypes)!);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Stfld, enumeratorItemField);
        il.Emit(OpCodes.Ret);

        var createEnumerator = listBuilder.DefineMethod("CreateEnumerator",
            MethodAttributes.Public,
            typeof(object),
            Type.EmptyTypes);
        var enumeratorOfT = enumeratorBuilder.MakeGenericType(listTypeParameter);
        var enumeratorCtorOfT = TypeBuilder.GetConstructor(enumeratorOfT, enumeratorCtor);
        il = createEnumerator.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, itemField);
        il.Emit(OpCodes.Newobj, enumeratorCtorOfT);
        il.Emit(OpCodes.Ret);

        enumeratorBuilder.CreateType();
        listBuilder.CreateType();
        asmBuilder.Save(outputPath);
    }

    private static void EmitPinnedLocalAssembly(string outputPath)
    {
        var asmName = new AssemblyName("PinnedLocalRoundTrip");
        var asmBuilder = new PersistedAssemblyBuilder(asmName, typeof(object).Assembly);
        var modBuilder = asmBuilder.DefineDynamicModule("PinnedLocalRoundTrip");
        var typeBuilder = modBuilder.DefineType("RoundTripTest.PinnedLocal",
            TypeAttributes.Public | TypeAttributes.Class | TypeAttributes.Sealed | TypeAttributes.Abstract);

        var methodBuilder = typeBuilder.DefineMethod("PinReference",
            MethodAttributes.Public | MethodAttributes.Static,
            typeof(int),
            new[] { typeof(int).MakeByRefType() });

        var il = methodBuilder.GetILGenerator();
        var pointerLocal = il.DeclareLocal(typeof(int).MakePointerType());
        var pinnedLocal = il.DeclareLocal(typeof(int).MakeByRefType(), pinned: true);

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Stloc, pinnedLocal);
        il.Emit(OpCodes.Ldloc, pinnedLocal);
        il.Emit(OpCodes.Conv_U);
        il.Emit(OpCodes.Stloc, pointerLocal);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Conv_U);
        il.Emit(OpCodes.Stloc, pinnedLocal);
        il.Emit(OpCodes.Ldc_I4, 42);
        il.Emit(OpCodes.Ret);

        typeBuilder.CreateType();
        asmBuilder.Save(outputPath);
    }

    /// <summary>
    /// Regression test: adding synthesized top-level types (MetadataToken == 0), as
    /// string hiding does, must not cause nested types to be emitted before their
    /// declaring types. If that happens the NestedClass row is missing and the
    /// runtime rejects the type with "format is invalid".
    /// </summary>
    [Fact]
    public void Write_ManyTypesPlusSynthesizedTypes_PreservesNestedTypeRelationship()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "wxsg_nested_order_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var asmPath = Path.Combine(tempDir, "NestedOrdering.dll");
        var rewrittenPath = Path.Combine(tempDir, "NestedOrdering_rw.dll");

        try
        {
            EmitNestedOrderingAssembly(asmPath, topLevelCount: 32);

            var reader = new MutableAssemblyReader();
            var assembly = reader.Read(asmPath, new MutableReaderParameters { ReadMethodBodies = true });

            // Simulate the helper types produced by string hiding. These have
            // MetadataToken == 0, which used to make the type ordering comparator
            // inconsistent and reorder nested types ahead of their declaring types.
            var guid = Guid.NewGuid().ToString().ToUpperInvariant();
            var objectType = assembly.MainModule.TypeSystem.Object;
            var layoutType = new MutableTypeDefinition("1{" + guid + "}", "2",
                TypeAttributes.ExplicitLayout | TypeAttributes.Sealed | TypeAttributes.NotPublic, objectType);
            layoutType.IsValueType = true;
            var helperType = new MutableTypeDefinition("<PrivateImplementationDetails>{" + guid + "}", guid,
                TypeAttributes.BeforeFieldInit | TypeAttributes.AutoClass | TypeAttributes.AnsiClass, objectType);
            assembly.MainModule.Types.Add(layoutType);
            assembly.MainModule.Types.Add(helperType);

            assembly.MainModule.FileName = rewrittenPath;
            new MutableAssemblyWriter(assembly).Write(rewrittenPath);

            using (var stream = File.OpenRead(rewrittenPath))
            using (var pe = new PEReader(stream))
            {
                var metadata = pe.GetMetadataReader();
                var nestedNames = metadata.TypeDefinitions
                    .Select(metadata.GetTypeDefinition)
                    .Where(t => !t.GetDeclaringType().IsNil)
                    .Select(t => metadata.GetString(t.Name))
                    .ToList();

                Assert.Contains("Nested", nestedNames);
                Assert.Contains("NestedGen", nestedNames);
            }

            var ctx = new AssemblyLoadContext("NestedOrdering_" + Guid.NewGuid(), isCollectible: true);
            Assembly loaded;
            try
            {
                loaded = ctx.LoadFromAssemblyPath(rewrittenPath);
            }
            catch (Exception ex)
            {
                ctx.Unload();
                Assert.Fail($"Failed to load round-tripped assembly: {ex}");
                return;
            }

            var loadException = Record.Exception(() => loaded.GetTypes());
            ctx.Unload();

            Assert.Null(loadException);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    private static void EmitNestedOrderingAssembly(string outputPath, int topLevelCount)
    {
        var asmName = new AssemblyName("NestedOrdering");
        var asmBuilder = new PersistedAssemblyBuilder(asmName, typeof(object).Assembly);
        var modBuilder = asmBuilder.DefineDynamicModule("NestedOrdering");

        for (int i = 0; i < topLevelCount; i++)
        {
            var typeBuilder = modBuilder.DefineType(
                $"RoundTripTest.T{i:D2}",
                TypeAttributes.Public | TypeAttributes.Class);

            if (i == 0)
            {
                typeBuilder.DefineNestedType("Nested", TypeAttributes.NestedPrivate | TypeAttributes.Class).CreateType();

                var nestedGeneric = typeBuilder.DefineNestedType("NestedGen", TypeAttributes.NestedPrivate | TypeAttributes.Class);
                nestedGeneric.DefineGenericParameters("T");
                nestedGeneric.CreateType();
            }

            typeBuilder.CreateType();
        }

        asmBuilder.Save(outputPath);
    }
}
