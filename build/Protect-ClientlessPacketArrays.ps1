# Scope: the direct ArraySerializer.Deserialize allocation for the exact
# PlayfieldAnarchyFMessage.PlayfieldDynelInfo type only. Its five 32-bit wire
# fields require 20 bytes per element. Other types follow Array.CreateInstance
# unchanged. The captured extended HQ variant is aligned before allocation;
# unknown/malformed layouts still pass through the original bounded guard.
function Protect-ClientlessPlayfieldArrayAllocation([string]$RuntimeDirectory) {
    # Instruction.Operand is object-typed. Keep these mutations inside a
    # typed .NET call: PowerShell wrappers must not reach Cecil's IL writer,
    # which casts operands directly to Instruction/MethodReference.
    if (-not ('CityDwellers.Build.CecilOperandsV3' -as [type])) {
        Add-Type -ReferencedAssemblies ([Mono.Cecil.Cil.Instruction].Assembly.Location) -TypeDefinition @'
using System;
using Mono.Cecil;
using Mono.Cecil.Cil;
namespace CityDwellers.Build
{
    public static class CecilOperandsV3
    {
        // Mono.Cecil's attribute enums are ushort-backed. PowerShell's -bor
        // routes two such operands through a dynamic call site and throws
        // InvalidCastException, so the flags are combined here instead.
        public static MethodAttributes GuardMethodAttributes
        {
            get { return MethodAttributes.Assembly | MethodAttributes.Static | MethodAttributes.HideBySig; }
        }

        private static string BodySignature(MethodDefinition method)
        {
            var parts = new System.Collections.Generic.List<string>();
            foreach (var variable in method.Body.Variables) parts.Add("local:" + variable.VariableType.FullName);
            foreach (var instruction in method.Body.Instructions)
            {
                var branch = instruction.Operand as Instruction;
                var member = instruction.Operand as MemberReference;
                var variable = instruction.Operand as VariableDefinition;
                string operand = branch != null ? "branch:" + method.Body.Instructions.IndexOf(branch) :
                    member != null ? member.FullName : variable != null ? "local:" + variable.Index :
                    Convert.ToString(instruction.Operand, System.Globalization.CultureInfo.InvariantCulture);
                parts.Add(instruction.OpCode.Code + ":" + operand);
            }
            return String.Join("|", parts);
        }

        private static bool SameMethod(MethodDefinition actual, MethodDefinition expected)
        {
            if (!actual.HasBody || actual.Attributes != expected.Attributes || actual.ReturnType.FullName != expected.ReturnType.FullName ||
                actual.Parameters.Count != expected.Parameters.Count || !actual.Body.InitLocals || actual.Body.ExceptionHandlers.Count != 0)
                return false;
            for (int i = 0; i < actual.Parameters.Count; i++)
                if (actual.Parameters[i].ParameterType.FullName != expected.Parameters[i].ParameterType.FullName) return false;
            return BodySignature(actual) == BodySignature(expected);
        }

        private static void Seek(ILProcessor il, VariableDefinition stream, VariableDefinition position, long delta, MethodReference setter)
        {
            il.Emit(OpCodes.Ldloc, stream);
            il.Emit(OpCodes.Ldloc, position);
            if (delta != 0) { il.Emit(OpCodes.Ldc_I8, delta); il.Emit(OpCodes.Add); }
            il.Emit(OpCodes.Callvirt, setter);
        }

        public static bool AlignExtendedHq(ModuleDefinition module, TypeDefinition reader, MethodDefinition deserialize,
            FieldDefinition streamField, TypeDefinition dynel, MethodDefinition bounded)
        {
            const string normalizeName = "CityDwellersNormalizeHqDynelLengthV1";
            const string wrapperName = "CityDwellersCreatePlayfieldDynelArrayHqV2";
            MethodDefinition readInt = null, priorNormalize = null, priorWrapper = null;
            foreach (var method in reader.Methods)
            {
                if (method.Name == "ReadInt32" && !method.IsStatic && method.Parameters.Count == 0 &&
                    method.ReturnType.FullName == "System.Int32")
                { if (readInt != null) throw new InvalidOperationException("Ambiguous wire integer reader."); readInt = method; }
                if (method.Name == normalizeName)
                { if (priorNormalize != null) throw new InvalidOperationException("Duplicate HQ normalizer."); priorNormalize = method; }
                if (method.Name == wrapperName)
                { if (priorWrapper != null) throw new InvalidOperationException("Duplicate HQ wrapper."); priorWrapper = method; }
            }
            if (readInt == null || (priorNormalize == null) != (priorWrapper == null))
                throw new InvalidOperationException("Wire reader or prior HQ patch changed.");
            MethodReference getType = null;
            foreach (var instruction in bounded.Body.Instructions)
            {
                var method = instruction.Operand as MethodReference;
                if (method != null && method.Name == "GetTypeFromHandle") getType = method;
            }
            if (getType == null) throw new InvalidOperationException("Bounded array type comparison changed.");
            var canSeek = new MethodReference("get_CanSeek", module.TypeSystem.Boolean, streamField.FieldType) { HasThis = true };
            var getLength = new MethodReference("get_Length", module.TypeSystem.Int64, streamField.FieldType) { HasThis = true };
            var getPosition = new MethodReference("get_Position", module.TypeSystem.Int64, streamField.FieldType) { HasThis = true };
            var setPosition = new MethodReference("set_Position", module.TypeSystem.Void, streamField.FieldType) { HasThis = true };
            setPosition.Parameters.Add(new ParameterDefinition(module.TypeSystem.Int64));
            var normalize = new MethodDefinition(normalizeName, GuardMethodAttributes, module.TypeSystem.Int32);
            normalize.Parameters.Add(new ParameterDefinition("elementType", ParameterAttributes.None, bounded.Parameters[0].ParameterType));
            normalize.Parameters.Add(new ParameterDefinition("length", ParameterAttributes.None, module.TypeSystem.Int32));
            normalize.Parameters.Add(new ParameterDefinition("reader", ParameterAttributes.None, reader));
            normalize.Body.InitLocals = true;
            normalize.Body.MaxStackSize = 4;
            var stream = new VariableDefinition(streamField.FieldType);
            var position = new VariableDefinition(module.TypeSystem.Int64);
            var packetLength = new VariableDefinition(module.TypeSystem.Int64);
            var count = new VariableDefinition(module.TypeSystem.Int32);
            normalize.Body.Variables.Add(stream); normalize.Body.Variables.Add(position);
            normalize.Body.Variables.Add(packetLength); normalize.Body.Variables.Add(count);
            var il = normalize.Body.GetILProcessor();
            var original = Instruction.Create(OpCodes.Ldarg_1);
            var restore = Instruction.Create(OpCodes.Nop);
            var extended = Instruction.Create(OpCodes.Nop);
            il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldtoken, dynel); il.Emit(OpCodes.Call, getType);
            il.Emit(OpCodes.Ceq); il.Emit(OpCodes.Brfalse, original);
            il.Emit(OpCodes.Ldarg_2); il.Emit(OpCodes.Ldfld, streamField); il.Emit(OpCodes.Stloc, stream);
            il.Emit(OpCodes.Ldloc, stream); il.Emit(OpCodes.Callvirt, canSeek); il.Emit(OpCodes.Brfalse, original);
            il.Emit(OpCodes.Ldloc, stream); il.Emit(OpCodes.Callvirt, getPosition); il.Emit(OpCodes.Stloc, position);
            il.Emit(OpCodes.Ldloc, stream); il.Emit(OpCodes.Callvirt, getLength); il.Emit(OpCodes.Stloc, packetLength);
            il.Emit(OpCodes.Ldloc, position); il.Emit(OpCodes.Ldc_I8, 68L); il.Emit(OpCodes.Blt, original);
            il.Emit(OpCodes.Ldloc, packetLength); il.Emit(OpCodes.Ldloc, position); il.Emit(OpCodes.Sub);
            il.Emit(OpCodes.Ldc_I8, 56L); il.Emit(OpCodes.Blt, original);
            // The supplied C77B / 28 / 4 HQ variant has twelve additional
            // opaque 32-bit words. Validate its full boundary before advancing.
            Seek(il, stream, position, -68L, setPosition);
            il.Emit(OpCodes.Ldarg_2); il.Emit(OpCodes.Call, readInt); il.Emit(OpCodes.Ldc_I4, 0xC77B); il.Emit(OpCodes.Bne_Un, restore);
            il.Emit(OpCodes.Ldarg_2); il.Emit(OpCodes.Call, readInt); il.Emit(OpCodes.Pop);
            il.Emit(OpCodes.Ldarg_2); il.Emit(OpCodes.Call, readInt); il.Emit(OpCodes.Ldc_I4, 28); il.Emit(OpCodes.Bne_Un, restore);
            il.Emit(OpCodes.Ldarg_2); il.Emit(OpCodes.Call, readInt); il.Emit(OpCodes.Ldc_I4, 4); il.Emit(OpCodes.Bne_Un, restore);
            // Preserve the legacy 48-byte layout when its count already fits.
            il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Blt, extended);
            il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Conv_I8); il.Emit(OpCodes.Ldc_I8, 20L); il.Emit(OpCodes.Mul);
            il.Emit(OpCodes.Ldc_I8, 8L); il.Emit(OpCodes.Add);
            il.Emit(OpCodes.Ldloc, packetLength); il.Emit(OpCodes.Ldloc, position); il.Emit(OpCodes.Sub);
            il.Emit(OpCodes.Beq, restore);
            il.Append(extended);
            Seek(il, stream, position, 44L, setPosition);
            il.Emit(OpCodes.Ldarg_2); il.Emit(OpCodes.Call, readInt); il.Emit(OpCodes.Stloc, count);
            il.Emit(OpCodes.Ldloc, count); il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Blt, restore);
            il.Emit(OpCodes.Ldloc, count); il.Emit(OpCodes.Conv_I8); il.Emit(OpCodes.Ldc_I8, 20L); il.Emit(OpCodes.Mul);
            il.Emit(OpCodes.Ldloc, position); il.Emit(OpCodes.Add); il.Emit(OpCodes.Ldc_I8, 56L); il.Emit(OpCodes.Add);
            il.Emit(OpCodes.Ldloc, packetLength); il.Emit(OpCodes.Bne_Un, restore);
            Seek(il, stream, packetLength, -8L, setPosition);
            il.Emit(OpCodes.Ldarg_2); il.Emit(OpCodes.Call, readInt); il.Emit(OpCodes.Ldc_I4_M1); il.Emit(OpCodes.Bne_Un, restore);
            il.Emit(OpCodes.Ldarg_2); il.Emit(OpCodes.Call, readInt); il.Emit(OpCodes.Ldc_I4_M1); il.Emit(OpCodes.Bne_Un, restore);
            Seek(il, stream, position, 48L, setPosition);
            il.Emit(OpCodes.Ldloc, count); il.Emit(OpCodes.Ret);
            il.Append(restore);
            Seek(il, stream, position, 0L, setPosition);
            il.Append(original); il.Emit(OpCodes.Ret);

            var wrapper = new MethodDefinition(wrapperName, GuardMethodAttributes, bounded.ReturnType);
            wrapper.Parameters.Add(new ParameterDefinition("elementType", ParameterAttributes.None, bounded.Parameters[0].ParameterType));
            wrapper.Parameters.Add(new ParameterDefinition("length", ParameterAttributes.None, new ByReferenceType(module.TypeSystem.Int32)));
            wrapper.Parameters.Add(new ParameterDefinition("reader", ParameterAttributes.None, reader));
            wrapper.Body.InitLocals = true; wrapper.Body.MaxStackSize = 4;
            var wi = wrapper.Body.GetILProcessor();
            wi.Emit(OpCodes.Ldarg_1); wi.Emit(OpCodes.Ldarg_0); wi.Emit(OpCodes.Ldarg_1); wi.Emit(OpCodes.Ldind_I4);
            wi.Emit(OpCodes.Ldarg_2); wi.Emit(OpCodes.Call, normalize); wi.Emit(OpCodes.Stind_I4);
            wi.Emit(OpCodes.Ldarg_0); wi.Emit(OpCodes.Ldarg_1); wi.Emit(OpCodes.Ldind_I4); wi.Emit(OpCodes.Ldarg_2);
            wi.Emit(OpCodes.Call, bounded); wi.Emit(OpCodes.Ret);

            Instruction call = null;
            foreach (var instruction in deserialize.Body.Instructions)
            {
                var method = instruction.Operand as MethodReference;
                if (method != null && method.DeclaringType.FullName == reader.FullName &&
                    (method.Name == bounded.Name || method.Name == wrapperName))
                { if (call != null) throw new InvalidOperationException("Ambiguous array guard call."); call = instruction; }
            }
            if (call == null || call.OpCode.Code != Code.Call || call.Previous == null ||
                call.Previous.OpCode.Code != Code.Ldarg_1 || call.Previous.Previous == null)
                throw new InvalidOperationException("Array guard call shape changed.");
            var load = call.Previous.Previous;
            if (priorWrapper != null)
            {
                if (((MethodReference)call.Operand).Name != wrapperName ||
                    (load.OpCode.Code != Code.Ldloca && load.OpCode.Code != Code.Ldloca_S) ||
                    !(load.Operand is VariableDefinition) || ((VariableDefinition)load.Operand).VariableType.FullName != "System.Int32" ||
                    !SameMethod(priorNormalize, normalize) || !SameMethod(priorWrapper, wrapper))
                    throw new InvalidOperationException("Existing HQ alignment patch changed.");
                return false;
            }
            VariableDefinition lengthVariable;
            switch (load.OpCode.Code)
            {
                case Code.Ldloc_0: lengthVariable = deserialize.Body.Variables[0]; break;
                case Code.Ldloc_1: lengthVariable = deserialize.Body.Variables[1]; break;
                case Code.Ldloc_2: lengthVariable = deserialize.Body.Variables[2]; break;
                case Code.Ldloc_3: lengthVariable = deserialize.Body.Variables[3]; break;
                case Code.Ldloc: case Code.Ldloc_S: lengthVariable = load.Operand as VariableDefinition; break;
                default: throw new InvalidOperationException("Expected local array length before allocation.");
            }
            if (lengthVariable == null || lengthVariable.VariableType.FullName != "System.Int32")
                throw new InvalidOperationException("Array length local changed.");
            // Pass the existing local by reference so both allocation AND the
            // deserializer loop use the corrected count, rather than the float.
            load.OpCode = OpCodes.Ldloca; load.Operand = lengthVariable;
            reader.Methods.Add(normalize); reader.Methods.Add(wrapper); call.Operand = wrapper;
            deserialize.Body.MaxStackSize = Math.Max(4, deserialize.Body.MaxStackSize);
            return true;
        }

        public static void ReplaceAllocation(MethodDefinition method, Instruction allocation, MethodDefinition guard)
        {
            if (method.Body.ExceptionHandlers.Count != 0)
                throw new InvalidOperationException("Array Deserialize exception regions changed.");
            var loadReader = Instruction.Create(OpCodes.Ldarg_1);
            foreach (var instruction in method.Body.Instructions)
            {
                if (Object.ReferenceEquals(instruction.Operand, allocation))
                    instruction.Operand = loadReader;
                var targets = instruction.Operand as Instruction[];
                if (targets != null)
                    for (int i = 0; i < targets.Length; i++)
                        if (Object.ReferenceEquals(targets[i], allocation)) targets[i] = loadReader;
                if (instruction.OpCode.OperandType == OperandType.ShortInlineBrTarget)
                {
                    string name = instruction.OpCode.Code.ToString();
                    if (!name.EndsWith("_S", StringComparison.Ordinal))
                        throw new InvalidOperationException("Unknown short branch opcode.");
                    var field = typeof(OpCodes).GetField(name.Substring(0, name.Length - 2));
                    if (field == null) throw new InvalidOperationException("Unable to widen array serializer branch.");
                    instruction.OpCode = (OpCode)field.GetValue(null);
                }
            }
            method.Body.GetILProcessor().InsertBefore(allocation, loadReader);
            allocation.Operand = guard;
            method.Body.MaxStackSize = Math.Max(3, method.Body.MaxStackSize + 1);
        }
    }
}
'@
    }
    $guardStage = 'opening dependency'
    $commonFiles = @(Get-ChildItem -LiteralPath $RuntimeDirectory -File |
        Where-Object { $_.Name -ieq 'AOSharp.Common.dll' })
    if ($commonFiles.Count -ne 1) { throw 'Expected exactly one AOSharp.Common.dll for the allocation guard.' }

    $readOptions = [Mono.Cecil.ReaderParameters]::new()
    $readOptions.InMemory = $true
    $readOptions.ReadSymbols = $false
    $commonAssembly = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($commonFiles[0].FullName, $readOptions)
    $guardTemp = Join-Path $RuntimeDirectory ('bounded-array-' + [Guid]::NewGuid().ToString('N') + '.tmp')
    $guardChanged = $false
    try {
        if ($commonAssembly.Name.HasPublicKey) { throw 'Refusing to rewrite signed AOSharp.Common.dll.' }
        $module = $commonAssembly.MainModule
        $prefix = 'SmokeLounge.AOtomation.Messaging.'
        $wireReader = $module.GetType($prefix + 'Serialization.StreamReader')
        $arraySerializer = $module.GetType($prefix + 'Serialization.Serializers.ArraySerializer')
        $dynelType = $module.GetType($prefix + 'Messages.N3Messages.PlayfieldAnarchyFMessage/PlayfieldDynelInfo')
        if ($null -eq $wireReader -or $null -eq $arraySerializer -or $null -eq $dynelType) {
            throw 'AOSharp wire serializer layout changed; allocation guard requires review.'
        }

        $guardStage = 'validating wire metadata'
        # Establish the actual wire width; do not infer a minimum byte count
        # for arbitrary serializer types or fabricate the unknown HQ layout.
        $expectedNames = @('IdentityType', 'Unknown1', 'Unknown2', 'Unknown3', 'Instance')
        if ($dynelType.BaseType.FullName -ne 'System.Object' -or $dynelType.Properties.Count -ne 5) {
            throw 'PlayfieldDynelInfo record shape changed; review its wire width.'
        }
        for ($index = 0; $index -lt 5; $index++) {
            $properties = @($dynelType.Properties | Where-Object { $_.Name -ceq $expectedNames[$index] })
            if ($properties.Count -ne 1) { throw 'PlayfieldDynelInfo property layout changed.' }
            $property = $properties[0]
            $memberAttrs = @($property.CustomAttributes | Where-Object {
                $_.AttributeType.FullName -ceq ($prefix + 'Serialization.MappingAttributes.AoMemberAttribute')
            })
            if ($memberAttrs.Count -ne 1 -or $memberAttrs[0].ConstructorArguments.Count -ne 1 -or
                [int]$memberAttrs[0].ConstructorArguments[0].Value -ne $index -or
                $memberAttrs[0].Properties.Count -ne 0 -or $memberAttrs[0].Fields.Count -ne 0 -or
                @($property.CustomAttributes | Where-Object {
                    $_.AttributeType.FullName -like ($prefix + 'Serialization.MappingAttributes.Ao*')
                }).Count -ne 1) {
                throw 'PlayfieldDynelInfo wire mapping changed; review its wire width.'
            }
            if ($index -eq 0) {
                $identityEnum = $module.GetType($property.PropertyType.FullName)
                if ($null -eq $identityEnum) { throw 'PlayfieldDynelInfo identity enum is not in the expected module.' }
                $valueFields = @($identityEnum.Fields | Where-Object { $_.Name -ceq 'value__' })
                if (!$identityEnum.IsEnum -or $valueFields.Count -ne 1 -or
                    $valueFields[0].FieldType.FullName -ne 'System.Int32') {
                    throw 'PlayfieldDynelInfo identity enum is no longer 32-bit.'
                }
            } elseif ($property.PropertyType.FullName -ne 'System.Int32') {
                throw 'PlayfieldDynelInfo field is no longer 32-bit.'
            }
        }

        $guardStage = 'validating serializer and reader'
        $streamFields = @($wireReader.Fields | Where-Object {
            $_.Name -ceq 'stream' -and !$_.IsStatic -and $_.FieldType.FullName -ceq 'System.IO.Stream'
        })
        $deserializeMethods = @($arraySerializer.Methods | Where-Object {
            $_.Name -ceq 'Deserialize' -and !$_.IsStatic -and $_.HasBody -and
            $_.ReturnType.FullName -ceq 'System.Object' -and $_.Parameters.Count -eq 3 -and
            $_.Parameters[0].ParameterType.FullName -ceq $wireReader.FullName -and
            $_.Parameters[1].ParameterType.FullName -ceq ($prefix + 'Serialization.SerializationContext') -and
            $_.Parameters[2].ParameterType.FullName -ceq ($prefix + 'Serialization.PropertyMetaData')
        })
        if ($streamFields.Count -ne 1 -or $deserializeMethods.Count -ne 1) {
            throw 'AOSharp reader/array method layout changed; allocation guard requires review.'
        }
        $streamField = $streamFields[0]
        $deserialize = $deserializeMethods[0]
        $markerName = 'CityDwellersCreatePlayfieldDynelArrayBoundedV1'
        $markers = @($wireReader.Methods | Where-Object { $_.Name -ceq $markerName })
        if ($markers.Count -gt 1) { throw 'Duplicate array allocation guard markers.' }
        $marker = if ($markers.Count -eq 1) { $markers[0] } else { $null }
        $arrayCreateSignature = 'System.Array System.Array::CreateInstance(System.Type,System.Int32)'
        $directCalls = @($deserialize.Body.Instructions | Where-Object {
            $_.OpCode.Code -eq [Mono.Cecil.Cil.Code]::Call -and $_.Operand -is [Mono.Cecil.MethodReference] -and
            $_.Operand.FullName -ceq $arrayCreateSignature
        })
        $guardCalls = @($deserialize.Body.Instructions | Where-Object {
            $_.Operand -is [Mono.Cecil.MethodReference] -and
            $_.Operand.DeclaringType.FullName -ceq $wireReader.FullName -and $_.Operand.Name -cin @($markerName, 'CityDwellersCreatePlayfieldDynelArrayHqV2')
        })
        if ($null -eq $marker) {
            if ($directCalls.Count -ne 1 -or $guardCalls.Count -ne 0) {
                throw 'Expected one unpatched direct Array.CreateInstance(Type,int) call.'
            }
            $createReference = $directCalls[0].Operand
        } else {
            if (!$marker.HasBody) { throw 'Existing allocation guard has no body.' }
            $markerCreates = @($marker.Body.Instructions | Where-Object {
                $_.OpCode.Code -eq [Mono.Cecil.Cil.Code]::Call -and $_.Operand -is [Mono.Cecil.MethodReference] -and
                $_.Operand.FullName -ceq $arrayCreateSignature
            })
            if ($directCalls.Count -ne 0 -or $guardCalls.Count -ne 1 -or $markerCreates.Count -ne 1 -or
                $guardCalls[0].OpCode.Code -ne [Mono.Cecil.Cil.Code]::Call -or
                $null -eq $guardCalls[0].Previous -or $guardCalls[0].Previous.OpCode.Code -ne [Mono.Cecil.Cil.Code]::Ldarg_1) {
                throw 'Partial or changed array allocation guard; refusing to layer another patch.'
            }
            $createReference = $markerCreates[0].Operand
        }

        $guardStage = 'constructing bounded allocation helper'
        # Use target-module references, never import the build host's runtime
        # assemblies (PowerShell editions can use different core libraries).
        $systemRefs = @($module.AssemblyReferences | Where-Object { $_.Name -ceq 'System' })
        if ($systemRefs.Count -ne 1) { throw 'Expected .NET Framework System reference for InvalidDataException.' }
        $badDataType = [Mono.Cecil.TypeReference]::new('System.IO', 'InvalidDataException', $module, $systemRefs[0])
        $badDataCtor = [Mono.Cecil.MethodReference]::new('.ctor', $module.TypeSystem.Void, $badDataType)
        $badDataCtor.HasThis = $true
        $badDataCtor.Parameters.Add([Mono.Cecil.ParameterDefinition]::new($module.TypeSystem.String))
        $runtimeTypeHandle = [Mono.Cecil.TypeReference]::new('System', 'RuntimeTypeHandle', $module, $module.TypeSystem.CoreLibrary, $true)
        $getType = [Mono.Cecil.MethodReference]::new('GetTypeFromHandle', $createReference.Parameters[0].ParameterType, $createReference.Parameters[0].ParameterType)
        $getType.Parameters.Add([Mono.Cecil.ParameterDefinition]::new($runtimeTypeHandle))
        $canSeek = [Mono.Cecil.MethodReference]::new('get_CanSeek', $module.TypeSystem.Boolean, $streamField.FieldType)
        $canSeek.HasThis = $true
        $getLength = [Mono.Cecil.MethodReference]::new('get_Length', $module.TypeSystem.Int64, $streamField.FieldType)
        $getLength.HasThis = $true
        $getPosition = [Mono.Cecil.MethodReference]::new('get_Position', $module.TypeSystem.Int64, $streamField.FieldType)
        $getPosition.HasThis = $true

        # Combined in the typed bridge: PowerShell cannot -bor a ushort-backed
        # enum. Do not inline this back into a PowerShell bitwise expression.
        $attributes = [CityDwellers.Build.CecilOperandsV3]::GuardMethodAttributes
        $expected = [Mono.Cecil.MethodDefinition]::new($markerName, $attributes, $createReference.ReturnType)
        $expected.Parameters.Add([Mono.Cecil.ParameterDefinition]::new('elementType', [Mono.Cecil.ParameterAttributes]::None, $createReference.Parameters[0].ParameterType))
        $expected.Parameters.Add([Mono.Cecil.ParameterDefinition]::new('length', [Mono.Cecil.ParameterAttributes]::None, $module.TypeSystem.Int32))
        $expected.Parameters.Add([Mono.Cecil.ParameterDefinition]::new('reader', [Mono.Cecil.ParameterAttributes]::None, $wireReader))
        $expected.Body.InitLocals = $true
        $expected.Body.MaxStackSize = 3
        $remaining = [Mono.Cecil.Cil.VariableDefinition]::new($module.TypeSystem.Int64)
        $expected.Body.Variables.Add($remaining)
        $il = $expected.Body.GetILProcessor()
        $allocate = $il.Create([Mono.Cecil.Cil.OpCodes]::Ldarg_0)
        $invalid = $il.Create([Mono.Cecil.Cil.OpCodes]::Ldstr, 'PlayfieldDynelInfo array length exceeds the remaining packet bytes.')
        $instructions = @(
            $il.Create([Mono.Cecil.Cil.OpCodes]::Ldarg_0)
            $il.Create([Mono.Cecil.Cil.OpCodes]::Ldtoken, [Mono.Cecil.TypeReference]$dynelType)
            $il.Create([Mono.Cecil.Cil.OpCodes]::Call, $getType)
            $il.Create([Mono.Cecil.Cil.OpCodes]::Ceq)
            $il.Create([Mono.Cecil.Cil.OpCodes]::Brfalse, $allocate)
            $il.Create([Mono.Cecil.Cil.OpCodes]::Ldarg_1)
            $il.Create([Mono.Cecil.Cil.OpCodes]::Ldc_I4_0)
            $il.Create([Mono.Cecil.Cil.OpCodes]::Blt, $invalid)
            $il.Create([Mono.Cecil.Cil.OpCodes]::Ldarg_2)
            $il.Create([Mono.Cecil.Cil.OpCodes]::Ldfld, $streamField)
            $il.Create([Mono.Cecil.Cil.OpCodes]::Callvirt, $canSeek)
            $il.Create([Mono.Cecil.Cil.OpCodes]::Brfalse, $allocate)
            $il.Create([Mono.Cecil.Cil.OpCodes]::Ldarg_2)
            $il.Create([Mono.Cecil.Cil.OpCodes]::Ldfld, $streamField)
            $il.Create([Mono.Cecil.Cil.OpCodes]::Callvirt, $getLength)
            $il.Create([Mono.Cecil.Cil.OpCodes]::Ldarg_2)
            $il.Create([Mono.Cecil.Cil.OpCodes]::Ldfld, $streamField)
            $il.Create([Mono.Cecil.Cil.OpCodes]::Callvirt, $getPosition)
            $il.Create([Mono.Cecil.Cil.OpCodes]::Sub)
            $il.Create([Mono.Cecil.Cil.OpCodes]::Stloc_0)
            $il.Create([Mono.Cecil.Cil.OpCodes]::Ldloc_0)
            $il.Create([Mono.Cecil.Cil.OpCodes]::Ldc_I4_0)
            $il.Create([Mono.Cecil.Cil.OpCodes]::Conv_I8)
            $il.Create([Mono.Cecil.Cil.OpCodes]::Blt, $invalid)
            $il.Create([Mono.Cecil.Cil.OpCodes]::Ldarg_1)
            $il.Create([Mono.Cecil.Cil.OpCodes]::Conv_I8)
            $il.Create([Mono.Cecil.Cil.OpCodes]::Ldloc_0)
            $il.Create([Mono.Cecil.Cil.OpCodes]::Ldc_I4, [int]20)
            $il.Create([Mono.Cecil.Cil.OpCodes]::Conv_I8)
            $il.Create([Mono.Cecil.Cil.OpCodes]::Div)
            $il.Create([Mono.Cecil.Cil.OpCodes]::Ble, $allocate)
            $invalid
            $il.Create([Mono.Cecil.Cil.OpCodes]::Newobj, $badDataCtor)
            $il.Create([Mono.Cecil.Cil.OpCodes]::Throw)
            $allocate
            $il.Create([Mono.Cecil.Cil.OpCodes]::Ldarg_1)
            $il.Create([Mono.Cecil.Cil.OpCodes]::Call, $createReference)
            $il.Create([Mono.Cecil.Cil.OpCodes]::Ret)
        )
        foreach ($instruction in $instructions) { $il.Append($instruction) }

        # A marker name alone is insufficient: a repeat build must retain the
        # exact guard body, signature, local type and callsite established here.
        function Get-ArrayGuardBodySignature($method) {
            $parts = [Collections.Generic.List[string]]::new()
            foreach ($instruction in $method.Body.Instructions) {
                $operand = $instruction.Operand
                $value = if ($operand -is [Mono.Cecil.Cil.Instruction]) {
                    'branch:' + $method.Body.Instructions.IndexOf($operand)
                } elseif ($operand -is [Mono.Cecil.MemberReference]) {
                    $operand.FullName
                } else { [string]$operand }
                $parts.Add([string]$instruction.OpCode.Code + ':' + $value)
            }
            return [string]::Join('|', $parts)
        }
        $guardStage = 'checking prior patch'
        if ($null -ne $marker) {
            if ($marker.Attributes -ne $expected.Attributes -or $marker.ReturnType.FullName -cne $expected.ReturnType.FullName -or
                $marker.Parameters.Count -ne 3 -or !$marker.HasBody -or !$marker.Body.InitLocals -or
                $marker.Body.ExceptionHandlers.Count -ne 0 -or $marker.Body.Variables.Count -ne 1 -or
                $marker.Body.Variables[0].VariableType.FullName -cne 'System.Int64') {
                throw 'Existing array allocation guard metadata changed.'
            }
            for ($index = 0; $index -lt 3; $index++) {
                if ($marker.Parameters[$index].ParameterType.FullName -cne $expected.Parameters[$index].ParameterType.FullName) {
                    throw 'Existing array allocation guard signature changed.'
                }
            }
            if ((Get-ArrayGuardBodySignature $marker) -cne (Get-ArrayGuardBodySignature $expected)) {
                throw 'Existing array allocation guard body changed.'
            }
        } else {
            $wireReader.Methods.Add($expected)
            $guardStage = 'rewriting allocation operands'
            [CityDwellers.Build.CecilOperandsV3]::ReplaceAllocation($deserialize, $directCalls[0], $expected)
            $guardChanged = $true
        }
        $guardStage = 'aligning the known extended HQ dynel block'
        $bounded = if ($null -ne $marker) { $marker } else { $expected }
        $hqChanged = [CityDwellers.Build.CecilOperandsV3]::AlignExtendedHq(
            $module, $wireReader, $deserialize, $streamField, $dynelType, $bounded)
        $guardChanged = $guardChanged -or $hqChanged
        if ($guardChanged) {
            $guardStage = 'writing guarded dependency'
            $commonAssembly.Write($guardTemp)
        }
    } catch {
        [Console]::Error.WriteLine("Packet allocation guard failed during: " + $guardStage)
        [Console]::Error.WriteLine($_.InvocationInfo.PositionMessage)
        [Console]::Error.WriteLine($_.ScriptStackTrace)
        [Console]::Error.WriteLine($_.Exception.ToString())
        if (Test-Path -LiteralPath $guardTemp) { Remove-Item -LiteralPath $guardTemp -Force -ErrorAction SilentlyContinue }
        throw
    } finally {
        $commonAssembly.Dispose()
    }
    try {
        if ($guardChanged) { Move-Item -LiteralPath $guardTemp -Destination $commonFiles[0].FullName -Force }
    } finally {
        if (Test-Path -LiteralPath $guardTemp) { Remove-Item -LiteralPath $guardTemp -Force }
    }
    Write-Host "Playfield array allocation guard and extended HQ alignment verified (newly applied: $guardChanged); existing allocation bounds retained."
}
