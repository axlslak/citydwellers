# Scope: the direct ArraySerializer.Deserialize allocation for the exact
# PlayfieldAnarchyFMessage.PlayfieldDynelInfo type only. Its five 32-bit wire
# fields require 20 bytes per element. Other types follow Array.CreateInstance
# unchanged. This prevents impossible allocation, NOT unsupported HQ decoding.
function Protect-ClientlessPlayfieldArrayAllocation([string]$RuntimeDirectory) {
    # Instruction.Operand is object-typed. Keep these mutations inside a
    # typed .NET call: PowerShell wrappers must not reach Cecil's IL writer,
    # which casts operands directly to Instruction/MethodReference.
    if (-not ('CityDwellers.Build.CecilOperandsV1' -as [type])) {
        Add-Type -ReferencedAssemblies ([Mono.Cecil.Cil.Instruction].Assembly.Location) -TypeDefinition @'
using System;
using Mono.Cecil;
using Mono.Cecil.Cil;
namespace CityDwellers.Build
{
    public static class CecilOperandsV1
    {
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
            $_.Operand.DeclaringType.FullName -ceq $wireReader.FullName -and $_.Operand.Name -ceq $markerName
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

        $attributes = [Mono.Cecil.MethodAttributes]::Assembly -bor [Mono.Cecil.MethodAttributes]::Static -bor [Mono.Cecil.MethodAttributes]::HideBySig
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
            [CityDwellers.Build.CecilOperandsV1]::ReplaceAllocation($deserialize, $directCalls[0], $expected)
            $guardChanged = $true
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
    Write-Host "Playfield array allocation guard verified (newly applied: $guardChanged); unsupported packet layout remains rejected."
}
