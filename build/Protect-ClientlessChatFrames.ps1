# Patch only the deployed dependency. TCP frame length includes its four-byte header.
function Protect-ClientlessChatFrames([string]$RuntimeDirectory) {
    if (-not ('CityDwellers.Build.ChatFramesV1' -as [type])) {
        Add-Type -ReferencedAssemblies ([Mono.Cecil.Cil.Instruction].Assembly.Location) -TypeDefinition @'
using System;
using System.Collections.Generic;
using Mono.Cecil;
using Mono.Cecil.Cil;
namespace CityDwellers.Build
{
    public static class ChatFramesV1
    {
        private static Instruction Next(Instruction instruction)
        {
            instruction = instruction.Next;
            while (instruction != null && instruction.OpCode.Code == Code.Nop) instruction = instruction.Next;
            return instruction;
        }
        private static bool AddsHeader(Instruction instruction)
        {
            Instruction constant = Next(instruction);
            return constant != null && constant.OpCode.Code == Code.Ldc_I4_4 &&
                Next(constant) != null && Next(constant).OpCode.Code == Code.Add;
        }
        public static bool Apply(ModuleDefinition module)
        {
            TypeDefinition type = module.GetType("AOSharp.Clientless.Chat.Net.TcpClientEx");
            if (type == null) throw new InvalidOperationException("Clientless chat TCP type changed.");
            bool header = false, buffer = false;
            foreach (FieldDefinition field in type.Fields)
            {
                if (field.Name == "HeaderSize" && field.HasConstant && Convert.ToInt32(field.Constant) == 4) header = true;
                if (field.Name == "_buffer" && field.FieldType.FullName == "System.Collections.Generic.List`1<System.Byte>") buffer = true;
            }
            MethodDefinition method = null;
            foreach (MethodDefinition candidate in type.Methods)
                if (candidate.Name == "ProcessBuffer" && candidate.Parameters.Count == 0 && candidate.HasBody)
                {
                    if (method != null) throw new InvalidOperationException("Ambiguous chat TCP processor.");
                    method = candidate;
                }
            if (!header || !buffer || method == null) throw new InvalidOperationException("Clientless chat frame layout changed.");
            var sizes = new List<Instruction>();
            foreach (Instruction instruction in method.Body.Instructions)
            {
                MethodReference member = instruction.Operand as MethodReference;
                if (member != null && member.Name == "get_Size" &&
                    member.DeclaringType.FullName == "SmokeLounge.AOtomation.Messaging.Messages.ChatHeader" &&
                    member.ReturnType.FullName == "System.UInt16") sizes.Add(instruction);
            }
            if (sizes.Count != 3 || !AddsHeader(sizes[1]) || !AddsHeader(sizes[2]))
                throw new InvalidOperationException("Clientless chat frame extents changed.");
            bool count = false;
            Instruction previous = sizes[0].Previous;
            for (int i = 0; previous != null && i < 6; i++, previous = previous.Previous)
            {
                MethodReference member = previous.Operand as MethodReference;
                if (member != null && member.Name == "get_Count" &&
                    member.DeclaringType.FullName == "System.Collections.Generic.List`1<System.Byte>") count = true;
            }
            Instruction comparison = Next(sizes[0]);
            if (AddsHeader(sizes[0])) comparison = Next(Next(comparison));
            if (!count || comparison == null ||
                !(comparison.OpCode.Code == Code.Bge || comparison.OpCode.Code == Code.Bge_S ||
                  comparison.OpCode.Code == Code.Blt || comparison.OpCode.Code == Code.Blt_S || comparison.OpCode.Code == Code.Clt))
                throw new InvalidOperationException("Clientless chat frame threshold changed.");
            if (AddsHeader(sizes[0])) return false;
            ILProcessor il = method.Body.GetILProcessor();
            Instruction four = il.Create(OpCodes.Ldc_I4_4);
            il.InsertAfter(sizes[0], four);
            il.InsertAfter(four, il.Create(OpCodes.Add));
            foreach (Instruction instruction in method.Body.Instructions)
            {
                switch (instruction.OpCode.Code)
                {
                    case Code.Br_S: instruction.OpCode = OpCodes.Br; break;
                    case Code.Brfalse_S: instruction.OpCode = OpCodes.Brfalse; break;
                    case Code.Brtrue_S: instruction.OpCode = OpCodes.Brtrue; break;
                    case Code.Beq_S: instruction.OpCode = OpCodes.Beq; break;
                    case Code.Bge_S: instruction.OpCode = OpCodes.Bge; break;
                    case Code.Bge_Un_S: instruction.OpCode = OpCodes.Bge_Un; break;
                    case Code.Bgt_S: instruction.OpCode = OpCodes.Bgt; break;
                    case Code.Bgt_Un_S: instruction.OpCode = OpCodes.Bgt_Un; break;
                    case Code.Ble_S: instruction.OpCode = OpCodes.Ble; break;
                    case Code.Ble_Un_S: instruction.OpCode = OpCodes.Ble_Un; break;
                    case Code.Blt_S: instruction.OpCode = OpCodes.Blt; break;
                    case Code.Blt_Un_S: instruction.OpCode = OpCodes.Blt_Un; break;
                    case Code.Bne_Un_S: instruction.OpCode = OpCodes.Bne_Un; break;
                    case Code.Leave_S: instruction.OpCode = OpCodes.Leave; break;
                }
            }
            method.Body.MaxStackSize++;
            return true;
        }
    }
}
'@
    }
    $files = @(Get-ChildItem -LiteralPath $RuntimeDirectory -File | Where-Object { $_.Name -ieq 'AOSharp.Clientless.Chat.dll' })
    if ($files.Count -ne 1) { throw 'Expected exactly one deployed Clientless chat assembly.' }
    $options = [Mono.Cecil.ReaderParameters]::new()
    $options.InMemory = $true
    $assembly = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($files[0].FullName, $options)
    $temporary = Join-Path $RuntimeDirectory ('chat-frames-' + [Guid]::NewGuid().ToString('N') + '.tmp')
    try {
        if ([CityDwellers.Build.ChatFramesV1]::Apply($assembly.MainModule)) {
            $assembly.Write($temporary)
            Move-Item -LiteralPath $temporary -Destination $files[0].FullName -Force
        }
    } finally {
        $assembly.Dispose()
        if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force }
    }
    Write-Host 'Clientless chat framing prepared: complete payload plus four-byte header required.'
}
