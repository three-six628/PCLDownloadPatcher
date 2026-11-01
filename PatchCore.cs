using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace PCLDownloadPatcher
{
    /// <summary>
    /// 下载修复核心：把新版 PCL 2.99.0.1 中被官方屏蔽的三处入口恢复为旧版逻辑。
    /// 修复 1：PageDownloadInstall.LoaderInit —— 下载列表 Loader 替换为 ModDownload.DlClientListLoader
    /// 修复 2：ModModpack.ModpackInstall(File, InstanceName, Logo) —— 恢复 switch 分发到 InstallPackXxx
    /// 修复 3：ModModpack.ModpackInstall() —— 重建无参入口（SelectFile + RunInThread + 闭包）
    /// </summary>
    public static class PatchCore
    {
        const string BlockedText = "为遵守相关法律法规，PCL 不再提供 MC 下载功能，非常抱歉 qwq……";

        public static void Patch(string srcPath, string dstPath, string hookPatcherPath)
        {
            Patch(srcPath, dstPath, hookPatcherPath, srcPath);
        }

        public static void Patch(string srcPath, string dstPath, string hookPatcherPath, string hookTargetPath)
        {
            string tmpPath = dstPath + ".tmp";
            string resDir = Path.Combine(Path.GetDirectoryName(dstPath), "refs");
            Directory.CreateDirectory(resDir);

            // 第一次读取：提取内嵌程序集（PCLCS 等）供写回时解析
            var rp0 = new ReaderParameters();
            var asm0 = AssemblyDefinition.ReadAssembly(srcPath, rp0);
            ExtractEmbeddedAssemblies(asm0, resDir);
            asm0.Dispose();

            // 带 resolver 重新读取
            var resolver = new DefaultAssemblyResolver();
            resolver.AddSearchDirectory(resDir);
            string fxDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows) + @"\Microsoft.NET\Framework64\v4.0.30319";
            if (Directory.Exists(fxDir)) resolver.AddSearchDirectory(fxDir);
            var rp = new ReaderParameters { AssemblyResolver = resolver };
            var asm = AssemblyDefinition.ReadAssembly(srcPath, rp);

            try
            {
                var ts = asm.MainModule.TypeSystem;
                var pdi = asm.MainModule.Types.First(t => t.FullName == "PCL.PageDownloadInstall");
                var modpack = asm.MainModule.Types.First(t => t.FullName == "PCL.ModModpack");
                var modDownload = asm.MainModule.Types.First(t => t.FullName == "PCL.ModDownload");

                Fix1_LoaderInit(asm, pdi, modDownload);
                Fix2_ModpackInstallParam(modpack);
                Fix3_ModpackInstallNoParam(asm, modpack);
                Fix4_ShowInstallEntry(asm);
                InjectUpdateHook(asm, hookTargetPath, hookPatcherPath);

                asm.Write(tmpPath, new WriterParameters());
            }
            finally
            {
                asm.Dispose();
            }

            if (File.Exists(dstPath)) File.Delete(dstPath);
            File.Move(tmpPath, dstPath);
        }

        // ---------- 更新自动修复钩子：DownloadLatestPCL 尾部最后一个 call（Move）后注入 call 修补器 ----------
        static void InjectUpdateHook(AssemblyDefinition asm, string dstPath, string hookPatcherPath)
        {
            if (string.IsNullOrEmpty(hookPatcherPath)) return;
            var ts = asm.MainModule.TypeSystem;
            MethodDefinition dlp = null;
            foreach (var t in asm.MainModule.Types)
            {
                foreach (var m in t.Methods)
                {
                    if (m.Name == "DownloadLatestPCL" && m.HasBody) { dlp = m; break; }
                }
                if (dlp != null) break;
            }
            if (dlp == null) return;

            // 尾部最后一个 call（Move 相关）
            Instruction lastCall = null;
            foreach (var ins in dlp.Body.Instructions)
            {
                var mr = ins.Operand as MethodReference;
                if (ins.OpCode == OpCodes.Call && mr != null) lastCall = ins;
            }
            if (lastCall == null) return;

            // Process::Start(String, String) 引用
            TypeReference procType = null;
            foreach (var tr in asm.MainModule.GetTypeReferences())
                if (tr.FullName == "System.Diagnostics.Process") { procType = tr; break; }
            if (procType == null) return;
            var start = new MethodReference("Start", procType, procType);
            start.Parameters.Add(new ParameterDefinition(ts.String));
            start.Parameters.Add(new ParameterDefinition(ts.String));

            string args = "--update --inplace \"" + dstPath + "\"";
            var proc = dlp.Body.GetILProcessor();
            var c1 = Instruction.Create(OpCodes.Ldstr, hookPatcherPath);
            var c2 = Instruction.Create(OpCodes.Ldstr, args);
            var c3 = Instruction.Create(OpCodes.Call, start);
            var c4 = Instruction.Create(OpCodes.Pop);
            proc.InsertAfter(lastCall, c1);
            proc.InsertAfter(c1, c2);
            proc.InsertAfter(c2, c3);
            proc.InsertAfter(c3, c4);
        }

        // ---------- 修复 1：LoaderInit ----------
        static void Fix1_LoaderInit(AssemblyDefinition asm, TypeDefinition pdi, TypeDefinition modDownload)
        {
            var li = pdi.Methods.First(m => m.Name == "LoaderInit");
            var ins = li.Body.Instructions;
            if (ins.Count < 23) throw new InvalidOperationException("LoaderInit 指令数异常: " + ins.Count);

            // 定位 [10] ldstr "关闭下载" 与 [22] newobj LoaderTask
            var t10 = ins[10];
            var dlField = modDownload.Fields.First(f => f.Name == "DlClientListLoader");
            var proc = li.Body.GetILProcessor();
            var targets = ins.Skip(10).Take(13).ToList();
            var sfld = Instruction.Create(OpCodes.Ldsfld, dlField);
            proc.InsertBefore(t10, sfld);
            foreach (var r in targets) proc.Remove(r);
            // 期望结果：18 条
            if (li.Body.Instructions.Count != 18)
                throw new InvalidOperationException("LoaderInit 修补后指令数异常: " + li.Body.Instructions.Count);
        }

        // ---------- 修复 2：有参 ModpackInstall(File, InstanceName, Logo) ----------
        static void Fix2_ModpackInstallParam(TypeDefinition modpack)
        {
            var mi = modpack.Methods.First(m => m.Name == "ModpackInstall" && m.Parameters.Count == 3);
            var ins = mi.Body.Instructions;

            // 找屏蔽异常三指令：ldstr 屏蔽文本 / newobj Exception / throw
            Instruction throwStr = null;
            foreach (var x in ins)
            {
                if (x.OpCode == OpCodes.Ldstr && (string)x.Operand == BlockedText) { throwStr = x; break; }
            }
            if (throwStr == null) throw new InvalidOperationException("修复2: 未找到屏蔽异常 ldstr");
            var throwNew = throwStr.Next;
            var throwOp = throwNew.Next;
            if (throwNew.OpCode != OpCodes.Newobj || throwOp.OpCode != OpCodes.Throw)
                throw new InvalidOperationException("修复2: 指令序列不符");

            var proc = mi.Body.GetILProcessor();
            // 变量：V0 = LoaderCombo<string>（返回值槽）、V4 = num
            var v0 = mi.Body.Variables[0];
            var v4 = mi.Body.Variables[4];
            // 追加 ret 作为新出口（最后 endfinally 之后，外层 finally handler 之外）
            var endFin = ins.Last(i => i.OpCode == OpCodes.Endfinally);
            var retNew = Instruction.Create(OpCodes.Ret);
            proc.Append(retNew);
            // 已知缺陷修正：leave 清栈后需先加载返回值槽 V0 再 ret（方法返回 LoaderCombo<string>）
            proc.InsertBefore(retNew, Instruction.Create(OpCodes.Ldloc, v0));
            // switch 插入锚点 = throwStr 原位（先插入 switch，最后再删除屏蔽三指令，避免 anchor 失效）
            var anchor = throwStr;

            // 分发方法引用（同模块）
            var fCurse = GetMethod(modpack, "InstallPackCurseForge", 5);
            var fHMCL = GetMethod(modpack, "InstallPackHMCL", 3);
            var fMMC = GetMethod(modpack, "InstallPackMMC", 3);
            var fMCBBS = GetMethod(modpack, "InstallPackMCBBS", 4);
            var fModr = GetMethod(modpack, "InstallPackModrinth", 5);
            var fLPack = GetMethod(modpack, "InstallPackLauncherPack", 3);
            var fComp = GetMethod(modpack, "InstallPackCompress", 2);

            // bne 比较链 + 各 case（leave -> retNew，触发外层 finally）
            var l1 = Instruction.Create(OpCodes.Nop); var l2 = Instruction.Create(OpCodes.Nop);
            var l3 = Instruction.Create(OpCodes.Nop); var l4 = Instruction.Create(OpCodes.Nop);
            var l5 = Instruction.Create(OpCodes.Nop); var l9 = Instruction.Create(OpCodes.Nop);

            // case 0: CurseForge(File,ZipArchive,archiveBaseFolder,InstanceName,Logo)
            EmitBranch(proc, anchor, v4, 0, l1);
            EmitCase(proc, anchor, fCurse, new OpCode[] { OpCodes.Ldarg_0, OpCodes.Ldloc_1, OpCodes.Ldloc_2, OpCodes.Ldarg_1, OpCodes.Ldarg_2 }, v0, retNew);
            proc.InsertBefore(anchor, l1);
            // case 1: HMCL(File,ZipArchive,baseFolder)
            EmitBranch(proc, anchor, v4, 1, l2);
            EmitCase(proc, anchor, fHMCL, new OpCode[] { OpCodes.Ldarg_0, OpCodes.Ldloc_1, OpCodes.Ldloc_2 }, v0, retNew);
            proc.InsertBefore(anchor, l2);
            // case 2: MMC(File,ZipArchive,baseFolder)
            EmitBranch(proc, anchor, v4, 2, l3);
            EmitCase(proc, anchor, fMMC, new OpCode[] { OpCodes.Ldarg_0, OpCodes.Ldloc_1, OpCodes.Ldloc_2 }, v0, retNew);
            proc.InsertBefore(anchor, l3);
            // case 3: MCBBS(File,Zip,baseFolder,InstanceName)
            EmitBranch(proc, anchor, v4, 3, l4);
            EmitCase(proc, anchor, fMCBBS, new OpCode[] { OpCodes.Ldarg_0, OpCodes.Ldloc_1, OpCodes.Ldloc_2, OpCodes.Ldarg_1 }, v0, retNew);
            proc.InsertBefore(anchor, l4);
            // case 4: Modrinth(File,Zip,baseFolder,InstanceName,Logo)
            EmitBranch(proc, anchor, v4, 4, l5);
            EmitCase(proc, anchor, fModr, new OpCode[] { OpCodes.Ldarg_0, OpCodes.Ldloc_1, OpCodes.Ldloc_2, OpCodes.Ldarg_1, OpCodes.Ldarg_2 }, v0, retNew);
            proc.InsertBefore(anchor, l5);
            // case 9: LauncherPack(File,Zip,baseFolder)
            EmitBranch(proc, anchor, v4, 9, l9);
            EmitCase(proc, anchor, fLPack, new OpCode[] { OpCodes.Ldarg_0, OpCodes.Ldloc_1, OpCodes.Ldloc_2 }, v0, retNew);
            proc.InsertBefore(anchor, l9);
            // default: Compress(File,Zip)
            EmitCase(proc, anchor, fComp, new OpCode[] { OpCodes.Ldarg_0, OpCodes.Ldloc_1 }, v0, retNew);
            // 最后删除屏蔽异常三指令（switch 已插入在原位之前）
            proc.Remove(throwStr);
            proc.Remove(throwNew);
            proc.Remove(throwOp);
        }

        static void EmitBranch(ILProcessor proc, Instruction anchor, VariableDefinition v4, int value, Instruction target)
        {
            proc.InsertBefore(anchor, Instruction.Create(OpCodes.Ldloc, v4));
            proc.InsertBefore(anchor, Instruction.Create(OpCodes.Ldc_I4, value));
            proc.InsertBefore(anchor, Instruction.Create(OpCodes.Bne_Un_S, target));
        }

        static void EmitCase(ILProcessor proc, Instruction anchor, MethodReference method, OpCode[] ldargs, VariableDefinition v0, Instruction leaveTarget)
        {
            foreach (var op in ldargs) proc.InsertBefore(anchor, Instruction.Create(op));
            proc.InsertBefore(anchor, Instruction.Create(OpCodes.Call, method));
            proc.InsertBefore(anchor, Instruction.Create(OpCodes.Stloc, v0));
            proc.InsertBefore(anchor, Instruction.Create(OpCodes.Leave, leaveTarget));
        }

        // ---------- 修复 3：无参 ModpackInstall() ----------
        static void Fix3_ModpackInstallNoParam(AssemblyDefinition asm, TypeDefinition modpack)
        {
            var ts = asm.MainModule.TypeSystem;
            var mi0 = modpack.Methods.First(m => m.Name == "ModpackInstall" && m.Parameters.Count == 0);
            var miStr = modpack.Methods.First(m => m.Name == "ModpackInstall" && m.Parameters.Count == 3);

            // 借异常处理引用（从程序集现有指令）
            MethodReference refSetErr = null, refClrErr = null, refThrCanc = null;
            foreach (var t in asm.MainModule.Types)
            {
                foreach (var m in t.Methods)
                {
                    if (!m.HasBody) continue;
                    foreach (var ins in m.Body.Instructions)
                    {
                        var mr = ins.Operand as MethodReference;
                        if (mr == null) continue;
                        if (mr.Name == "SetProjectError" && refSetErr == null) refSetErr = mr;
                        else if (mr.Name == "ClearProjectError" && refClrErr == null) refClrErr = mr;
                        else if (mr.Name == "ThrowIfCanceled" && refThrCanc == null) refThrCanc = mr;
                    }
                }
            }
            if (refSetErr == null || refClrErr == null || refThrCanc == null)
                throw new InvalidOperationException("修复3: 异常处理引用缺失");

            var exRef = asm.MainModule.GetTypeReferences().First(t => t.FullName == "System.Exception");

            // 闭包类 _Closure$__DlFix（嵌套于 ModModpack）
            var nested = new TypeDefinition("", "_Closure$__DlFix",
                TypeAttributes.NestedPrivate | TypeAttributes.Sealed | TypeAttributes.BeforeFieldInit, ts.Object);
            modpack.NestedTypes.Add(nested);
            var fText = new FieldDefinition("text", FieldAttributes.Public, ts.String);
            nested.Fields.Add(fText);

            // 闭包 .ctor
            var objCtor = new MethodReference(".ctor", ts.Void, ts.Object) { HasThis = true };
            var ctor = new MethodDefinition(".ctor",
                MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName,
                ts.Void);
            ctor.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0));
            ctor.Body.Instructions.Add(Instruction.Create(OpCodes.Call, objCtor));
            ctor.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
            nested.Methods.Add(ctor);

            // 闭包 b__0：try { ldarg.0; ldfld text; call ModpackInstall(string); pop } catch(Exception) { SetProjectError; ThrowIfCanceled; ClearProjectError } leave ret
            var b0 = new MethodDefinition("b__0", MethodAttributes.Public | MethodAttributes.HideBySig, ts.Void);
            var pb = b0.Body.GetILProcessor();
            var vEx = new VariableDefinition(ts.Object);
            b0.Body.Variables.Add(vEx);
            var tLd = Instruction.Create(OpCodes.Ldarg_0);
            var tLdf = Instruction.Create(OpCodes.Ldfld, fText);
            var tCall = Instruction.Create(OpCodes.Call, miStr);
            var tPop = Instruction.Create(OpCodes.Pop);
            var hDup = Instruction.Create(OpCodes.Dup);
            var hSet = Instruction.Create(OpCodes.Call, refSetErr);
            var hSt = Instruction.Create(OpCodes.Stloc, vEx);
            var hLd = Instruction.Create(OpCodes.Ldloc, vEx);
            var hThr = Instruction.Create(OpCodes.Call, refThrCanc);
            var hClr = Instruction.Create(OpCodes.Call, refClrErr);
            var retB = Instruction.Create(OpCodes.Ret);
            var tLeave = Instruction.Create(OpCodes.Leave, retB);
            var hLeave = Instruction.Create(OpCodes.Leave, retB);
            pb.Append(tLd); pb.Append(tLdf); pb.Append(tCall); pb.Append(tPop);
            pb.Append(tLeave);
            pb.Append(hDup); pb.Append(hSet); pb.Append(hSt); pb.Append(hLd); pb.Append(hThr); pb.Append(hClr); pb.Append(hLeave);
            pb.Append(retB);
            var eh = new ExceptionHandler(ExceptionHandlerType.Catch)
            {
                TryStart = tLd, TryEnd = hDup, HandlerStart = hDup, HandlerEnd = retB, CatchType = exRef
            };
            b0.Body.ExceptionHandlers.Add(eh);
            nested.Methods.Add(b0);

            // 引用：SelectFile（从 PageSetupUI.BtnLogoChange_Click 借）、OneOf op_Implicit、Nullable ctor、RunInThread
            MethodReference selFile = null, opImplicit = null, nullableCtor = null, vtcCtor = null, runInThread = null;
            foreach (var t in asm.MainModule.Types)
            {
                foreach (var m in t.Methods)
                {
                    if (!m.HasBody) continue;
                    foreach (var ins in m.Body.Instructions)
                    {
                        var mr = ins.Operand as MethodReference;
                        if (mr == null) continue;
                        if (mr.DeclaringType.FullName == "MeloongCore.Wpf.Dialogs" && mr.Name == "SelectFile" && selFile == null) selFile = mr;
                        else if (mr.Name == "op_Implicit" && mr.DeclaringType.FullName.StartsWith("MeloongCore.OneOf") && opImplicit == null) opImplicit = mr;
                        else if (mr.Name == ".ctor" && mr.DeclaringType.FullName.StartsWith("System.Nullable`1<MeloongCore.OneOf") && nullableCtor == null) nullableCtor = mr;
                        else if (mr.Name == ".ctor" && mr.DeclaringType.FullName.StartsWith("System.ValueTuple`2<System.String[]") && vtcCtor == null) vtcCtor = mr;
                        else if (mr.DeclaringType.FullName == "PCL.ModBase" && mr.Name == "RunInThread" && runInThread == null) runInThread = mr;
                    }
                }
            }
            if (selFile == null || opImplicit == null || nullableCtor == null || vtcCtor == null || runInThread == null)
                throw new InvalidOperationException("修复3: SelectFile 链路引用缺失 sel=" + (selFile != null) + " imp=" + (opImplicit != null) + " nul=" + (nullableCtor != null) + " vt=" + (vtcCtor != null) + " run=" + (runInThread != null));

            // List`1 泛型实例 get_Count / get_Item
            var listRef = asm.MainModule.GetTypeReferences().First(t => t.FullName == "System.Collections.Generic.List`1");
            var gList = new GenericInstanceType(listRef);
            gList.GenericArguments.Add(ts.String);
            var listCountG = new MethodReference("get_Count", ts.Int32, gList) { HasThis = true };
            var listItemG = new MethodReference("get_Item", ts.String, gList) { HasThis = true };
            listItemG.Parameters.Add(new ParameterDefinition(ts.Int32));

            // String.IsNullOrEmpty
            var isNullOrEmpty = new MethodReference("IsNullOrEmpty", ts.Boolean, ts.String);
            isNullOrEmpty.Parameters.Add(new ParameterDefinition(ts.String));

            // Action::.ctor(object, IntPtr)
            var actionType = asm.MainModule.GetTypeReferences().First(t => t.FullName == "System.Action");
            var actionCtor = new MethodReference(".ctor", ts.Void, actionType) { HasThis = true };
            actionCtor.Parameters.Add(new ParameterDefinition(ts.Object));
            actionCtor.Parameters.Add(new ParameterDefinition(ts.IntPtr));

            // 重建无参方法（先清空原 7 条屏蔽指令与 EH）
            mi0.Body.Instructions.Clear();
            mi0.Body.ExceptionHandlers.Clear();
            var vDc = new VariableDefinition(nested);           // V0 = display class
            mi0.Body.Variables.Add(vDc);
            var vList = new VariableDefinition(gList);          // V1 = SelectFile 返回值 List<string>
            mi0.Body.Variables.Add(vList);
            var p0 = mi0.Body.GetILProcessor();
            var iExit = Instruction.Create(OpCodes.Ret);

            p0.Append(Instruction.Create(OpCodes.Newobj, ctor));
            p0.Append(Instruction.Create(OpCodes.Stloc, vDc));
            p0.Append(Instruction.Create(OpCodes.Ldloc, vDc));
            p0.Append(Instruction.Create(OpCodes.Ldstr, "选择整合包压缩文件"));
            p0.Append(Instruction.Create(OpCodes.Ldc_I4_0));
            p0.Append(Instruction.Create(OpCodes.Ldnull));

            // new (ValueTuple<string[],string>[]){ (new[]{"rar","zip","mrpack"}, "整合包文件") }
            var vtType = new GenericInstanceType(vtcCtor.DeclaringType);
            var strArr = new ArrayType(ts.String);
            var arrType = new ArrayType(vtType);
            p0.Append(Instruction.Create(OpCodes.Ldc_I4_1));
            p0.Append(Instruction.Create(OpCodes.Newarr, vtType));
            p0.Append(Instruction.Create(OpCodes.Dup));
            p0.Append(Instruction.Create(OpCodes.Ldc_I4_0));
            p0.Append(Instruction.Create(OpCodes.Ldc_I4_3));
            p0.Append(Instruction.Create(OpCodes.Newarr, ts.String));
            p0.Append(Instruction.Create(OpCodes.Dup));
            p0.Append(Instruction.Create(OpCodes.Ldc_I4_0));
            p0.Append(Instruction.Create(OpCodes.Ldstr, "rar"));
            p0.Append(Instruction.Create(OpCodes.Stelem_Ref));
            p0.Append(Instruction.Create(OpCodes.Dup));
            p0.Append(Instruction.Create(OpCodes.Ldc_I4_1));
            p0.Append(Instruction.Create(OpCodes.Ldstr, "zip"));
            p0.Append(Instruction.Create(OpCodes.Stelem_Ref));
            p0.Append(Instruction.Create(OpCodes.Dup));
            p0.Append(Instruction.Create(OpCodes.Ldc_I4_2));
            p0.Append(Instruction.Create(OpCodes.Ldstr, "mrpack"));
            p0.Append(Instruction.Create(OpCodes.Stelem_Ref));
            p0.Append(Instruction.Create(OpCodes.Ldstr, "整合包文件"));
            p0.Append(Instruction.Create(OpCodes.Newobj, vtcCtor));
            p0.Append(Instruction.Create(OpCodes.Stelem_Any, vtType));
            p0.Append(Instruction.Create(OpCodes.Call, opImplicit));
            p0.Append(Instruction.Create(OpCodes.Newobj, nullableCtor));
            p0.Append(Instruction.Create(OpCodes.Call, selFile));
            p0.Append(Instruction.Create(OpCodes.Stloc, vList));

            // 空检查
            p0.Append(Instruction.Create(OpCodes.Ldloc, vList));
            p0.Append(Instruction.Create(OpCodes.Brfalse, iExit));
            p0.Append(Instruction.Create(OpCodes.Ldloc, vList));
            p0.Append(Instruction.Create(OpCodes.Callvirt, listCountG));
            p0.Append(Instruction.Create(OpCodes.Ldc_I4_0));
            p0.Append(Instruction.Create(OpCodes.Ble, iExit));
            p0.Append(Instruction.Create(OpCodes.Ldloc, vList));
            p0.Append(Instruction.Create(OpCodes.Ldc_I4_0));
            p0.Append(Instruction.Create(OpCodes.Callvirt, listItemG));
            p0.Append(Instruction.Create(OpCodes.Stfld, fText));

            // IsNullOrEmpty 检查
            p0.Append(Instruction.Create(OpCodes.Ldloc, vDc));
            p0.Append(Instruction.Create(OpCodes.Ldfld, fText));
            p0.Append(Instruction.Create(OpCodes.Call, isNullOrEmpty));
            p0.Append(Instruction.Create(OpCodes.Brtrue, iExit));

            // RunInThread(new Action(b__0))
            p0.Append(Instruction.Create(OpCodes.Ldloc, vDc));
            p0.Append(Instruction.Create(OpCodes.Ldftn, b0));
            p0.Append(Instruction.Create(OpCodes.Newobj, actionCtor));
            p0.Append(Instruction.Create(OpCodes.Call, runInThread));
            p0.Append(iExit);
        }

        // ---------- 修复4：恢复下载页左侧"原版游戏"入口（PageDownloadLeft..ctor 删除 ItemInstall 的 Collapsed） ----------
        static void Fix4_ShowInstallEntry(AssemblyDefinition asm)
        {
            var t = asm.MainModule.Types.First(x => x.FullName == "PCL.PageDownloadLeft");
            var ctor = t.Methods.First(m => m.IsConstructor);
            var ins = ctor.Body.Instructions;
            for (int i = 0; i < ins.Count - 3; i++)
            {
                var mr1 = ins[i + 1].Operand as MethodReference;
                var mr3 = ins[i + 3].Operand as MethodReference;
                if (ins[i].OpCode == OpCodes.Ldarg_0 &&
                    ins[i + 1].OpCode == OpCodes.Callvirt && mr1 != null && mr1.Name == "get_ItemInstall" &&
                    ins[i + 2].OpCode == OpCodes.Ldc_I4_2 &&
                    ins[i + 3].OpCode == OpCodes.Callvirt && mr3 != null && mr3.Name == "set_Visibility")
                {
                    // 命中：ldarg.0; callvirt get_ItemInstall; ldc.i4.2(Collapsed); callvirt set_Visibility —— 删除这4条
                    var toRemove = new[] { ins[i], ins[i + 1], ins[i + 2], ins[i + 3] };
                    foreach (var r in toRemove) ins.Remove(r);
                    return;
                }
            }
            throw new InvalidOperationException("修复4: 未找到 ItemInstall 屏蔽段");
        }

        static MethodDefinition GetMethod(TypeDefinition t, string name, int pc)
        {
            return t.Methods.First(m => m.Name == name && m.Parameters.Count == pc);
        }

        static void ExtractEmbeddedAssemblies(AssemblyDefinition asm, string resDir)
        {
            var res = asm.MainModule.Resources.FirstOrDefault(r => r.Name == "PCL.Resources.resources") as EmbeddedResource;
            if (res == null) return;
            using (var rs = new System.Resources.ResourceReader(res.GetResourceStream()))
            {
                var en = rs.GetEnumerator();
                while (en.MoveNext())
                {
                    var de = (System.Collections.DictionaryEntry)en.Current;
                    var key = (string)de.Key;
                    var bytes = de.Value as byte[];
                    if (bytes != null && bytes.Length > 1000)
                    {
                        var path = Path.Combine(resDir, key + ".dll");
                        File.WriteAllBytes(path, bytes);
                    }
                }
            }
        }
    }
}
