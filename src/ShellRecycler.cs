using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace BrowserAssistScanner
{
    // The program owns this implementation. No external DeleteAllVars command is required.
    public sealed class ShellRecycler : IRecycler
    {
        public const uint RecycleFlag = 0x80;
        private const uint Flags = 0x00080000 | 0x20000000 | 0x2000 | 0x0004 | 0x0010 | 0x0400 | 0x4000;
        internal static string ShellPath(IShellItem item)
        {
            IntPtr value;
            Marshal.ThrowExceptionForHR(item.GetDisplayName(0x80058000, out value));
            try { return DeleteScope.Normalize(Marshal.PtrToStringUni(value)); }
            finally { Marshal.FreeCoTaskMem(value); }
        }
        public static int CheckDelete(uint flags, string actual, DeleteTarget expected, DeleteScope scope, bool emptyOnly)
        {
            try
            {
                if ((flags & RecycleFlag) == 0) return unchecked((int)0x80070032); // Reject permanent-delete fallback.
                actual = DeleteScope.Normalize(actual);
                bool exact = String.Equals(actual, expected.Path, StringComparison.OrdinalIgnoreCase);
                if (!exact && (emptyOnly || !expected.IsDirectory || !DeleteScope.Descendant(actual, expected.Path))) return unchecked((int)0x80070005);
                scope.Validate(actual, exact ? (bool?)expected.IsDirectory : null);
                if (emptyOnly && Directory.Exists(actual) && Directory.EnumerateFileSystemEntries(actual).Any()) return unchecked((int)0x80070091);
                return 0;
            }
            catch { return unchecked((int)0x80070005); }
        }
        public void Recycle(IList<DeleteTarget> targets, DeleteScope scope, bool emptyDirectoriesOnly,
            CancellationToken cancel, Action<string> progress, DeleteReport report)
        {
            if (cancel.IsCancellationRequested) { report.Cancelled = true; return; }
            IFileOperation operation = null;
            var queued = new List<RecycleSink>();
            int performed = 0;
            try
            {
                operation = (IFileOperation)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("3AD05575-8857-4850-9277-11B85BDB8E09")));
                Marshal.ThrowExceptionForHR(operation.SetOperationFlags(Flags | (emptyDirectoriesOnly ? 0x1000U : 0U)));
                foreach (var target in targets)
                {
                    if (cancel.IsCancellationRequested) { report.Cancelled = true; break; }
                    IShellItem item = null;
                    try
                    {
                        scope.Validate(target.Path, target.IsDirectory);
                        // Use attributes so access errors are not reported as nonexistent files.
                        try { File.GetAttributes(target.Path); }
                        catch (FileNotFoundException) { report.Missing++; continue; }
                        catch (DirectoryNotFoundException) { report.Missing++; continue; }
                        Guid iid = typeof(IShellItem).GUID;
                        Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(target.Path, IntPtr.Zero, ref iid, out item));
                        var sink = new RecycleSink(target, scope, emptyDirectoriesOnly, cancel, progress);
                        Marshal.ThrowExceptionForHR(operation.DeleteItem(item, sink));
                        queued.Add(sink);
                    }
                    catch (Exception error) { report.Issues.Add(target.Path + "：" + error.Message); }
                    finally { if (item != null) Marshal.ReleaseComObject(item); }
                }
                if (queued.Count > 0 && !cancel.IsCancellationRequested) performed = operation.PerformOperations();
                bool aborted;
                int query = operation.GetAnyOperationsAborted(out aborted);
                if (query < 0) performed = query;
                foreach (var sink in queued)
                {
                    bool gone = false;
                    try { File.GetAttributes(sink.Target.Path); }
                    catch (FileNotFoundException) { gone = true; }
                    catch (DirectoryNotFoundException) { gone = true; }
                    catch { }
                    if (sink.Completed && sink.Result >= 0 && sink.Veto >= 0 && gone) report.Recycled++;
                    else if (cancel.IsCancellationRequested && !sink.Completed) report.Cancelled = true;
                    else {
                        int error = sink.Veto < 0 ? sink.Veto : sink.Result < 0 ? sink.Result : performed < 0 ? performed : unchecked((int)0x80004005);
                        string message = Marshal.GetExceptionForHR(error).Message;
                        report.Issues.Add(sink.Target.Path + "：未回收，" + message);
                    }
                }
                if (cancel.IsCancellationRequested) report.Cancelled = true;
            }
            catch (Exception error) { report.Issues.Add("Windows 回收站操作失败：" + error.Message); }
            finally { if (operation != null) Marshal.ReleaseComObject(operation); }
        }

        [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
        public sealed class RecycleSink : IFileOperationProgressSink
        {
            public readonly DeleteTarget Target;
            private readonly DeleteScope scope;
            private readonly bool emptyOnly;
            private readonly CancellationToken cancel;
            private readonly Action<string> progress;
            public bool Completed;
            public int Result = unchecked((int)0x80004005), Veto;
            public RecycleSink(DeleteTarget target, DeleteScope scope, bool emptyOnly, CancellationToken cancel, Action<string> progress)
            { Target = target; this.scope = scope; this.emptyOnly = emptyOnly; this.cancel = cancel; this.progress = progress; }
            public int StartOperations() { return 0; }
            public int FinishOperations(int result) { return 0; }
            public int PreRenameItem(uint flags, IShellItem item, string name) { return unchecked((int)0x80004001); }
            public int PostRenameItem(uint flags, IShellItem item, string name, int result, IShellItem created) { return 0; }
            public int PreMoveItem(uint flags, IShellItem item, IShellItem folder, string name) { return unchecked((int)0x80004001); }
            public int PostMoveItem(uint flags, IShellItem item, IShellItem folder, string name, int result, IShellItem created) { return 0; }
            public int PreCopyItem(uint flags, IShellItem item, IShellItem folder, string name) { return unchecked((int)0x80004001); }
            public int PostCopyItem(uint flags, IShellItem item, IShellItem folder, string name, int result, IShellItem created) { return 0; }
            public int PreDeleteItem(uint flags, IShellItem item)
            {
                if (cancel.IsCancellationRequested) return Veto = unchecked((int)0x80004004);
                try
                {
                    string path = ShellPath(item);
                    int allowed = CheckDelete(flags, path, Target, scope, emptyOnly);
                    if (allowed < 0) return Veto = allowed;
                    progress("正在回收：" + path);
                    return 0;
                }
                catch { return Veto = unchecked((int)0x80070005); }
            }
            public int PostDeleteItem(uint flags, IShellItem item, int result, IShellItem created)
            {
                try { if (String.Equals(ShellPath(item), Target.Path, StringComparison.OrdinalIgnoreCase)) { Completed = true; Result = result; } }
                catch { Completed = true; Result = result; }
                return 0;
            }
            public int PreNewItem(uint flags, IShellItem folder, string name) { return unchecked((int)0x80004001); }
            public int PostNewItem(uint flags, IShellItem folder, string name, string template, uint attributes, int result, IShellItem created) { return 0; }
            public int UpdateProgress(uint total, uint done) { return 0; }
            public int ResetTimer() { return 0; }
            public int PauseTimer() { return 0; }
            public int ResumeTimer() { return 0; }
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
        private static extern int SHCreateItemFromParsingName(string path, IntPtr bind, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IShellItem item);
    }

    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IShellItem
    {
        [PreserveSig] int BindToHandler(IntPtr bind, ref Guid handler, ref Guid iid, out IntPtr result);
        [PreserveSig] int GetParent(out IShellItem parent);
        [PreserveSig] int GetDisplayName(uint kind, out IntPtr name);
        [PreserveSig] int GetAttributes(uint mask, out uint attributes);
        [PreserveSig] int Compare(IShellItem other, uint hint, out int order);
    }

    [ComVisible(true), Guid("04B0F1A7-9490-44BC-96E1-4296A31252E2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IFileOperationProgressSink
    {
        [PreserveSig] int StartOperations();
        [PreserveSig] int FinishOperations(int result);
        [PreserveSig] int PreRenameItem(uint flags, IShellItem item, [MarshalAs(UnmanagedType.LPWStr)] string name);
        [PreserveSig] int PostRenameItem(uint flags, IShellItem item, [MarshalAs(UnmanagedType.LPWStr)] string name, int result, IShellItem created);
        [PreserveSig] int PreMoveItem(uint flags, IShellItem item, IShellItem folder, [MarshalAs(UnmanagedType.LPWStr)] string name);
        [PreserveSig] int PostMoveItem(uint flags, IShellItem item, IShellItem folder, [MarshalAs(UnmanagedType.LPWStr)] string name, int result, IShellItem created);
        [PreserveSig] int PreCopyItem(uint flags, IShellItem item, IShellItem folder, [MarshalAs(UnmanagedType.LPWStr)] string name);
        [PreserveSig] int PostCopyItem(uint flags, IShellItem item, IShellItem folder, [MarshalAs(UnmanagedType.LPWStr)] string name, int result, IShellItem created);
        [PreserveSig] int PreDeleteItem(uint flags, IShellItem item);
        [PreserveSig] int PostDeleteItem(uint flags, IShellItem item, int result, IShellItem created);
        [PreserveSig] int PreNewItem(uint flags, IShellItem folder, [MarshalAs(UnmanagedType.LPWStr)] string name);
        [PreserveSig] int PostNewItem(uint flags, IShellItem folder, [MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.LPWStr)] string template, uint attributes, int result, IShellItem created);
        [PreserveSig] int UpdateProgress(uint total, uint done);
        [PreserveSig] int ResetTimer();
        [PreserveSig] int PauseTimer();
        [PreserveSig] int ResumeTimer();
    }

    [ComImport, Guid("947AAB5F-0A5C-4C13-B4D6-4BF7836FC9F8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IFileOperation
    {
        [PreserveSig] int Advise(IFileOperationProgressSink sink, out uint cookie);
        [PreserveSig] int Unadvise(uint cookie);
        [PreserveSig] int SetOperationFlags(uint flags);
        [PreserveSig] int SetProgressMessage([MarshalAs(UnmanagedType.LPWStr)] string text);
        [PreserveSig] int SetProgressDialog([MarshalAs(UnmanagedType.Interface)] object dialog);
        [PreserveSig] int SetProperties([MarshalAs(UnmanagedType.Interface)] object properties);
        [PreserveSig] int SetOwnerWindow(IntPtr window);
        [PreserveSig] int ApplyPropertiesToItem(IShellItem item);
        [PreserveSig] int ApplyPropertiesToItems([MarshalAs(UnmanagedType.Interface)] object items);
        [PreserveSig] int RenameItem(IShellItem item, [MarshalAs(UnmanagedType.LPWStr)] string name, IFileOperationProgressSink sink);
        [PreserveSig] int RenameItems([MarshalAs(UnmanagedType.Interface)] object items, [MarshalAs(UnmanagedType.LPWStr)] string name);
        [PreserveSig] int MoveItem(IShellItem item, IShellItem folder, [MarshalAs(UnmanagedType.LPWStr)] string name, IFileOperationProgressSink sink);
        [PreserveSig] int MoveItems([MarshalAs(UnmanagedType.Interface)] object items, IShellItem folder);
        [PreserveSig] int CopyItem(IShellItem item, IShellItem folder, [MarshalAs(UnmanagedType.LPWStr)] string name, IFileOperationProgressSink sink);
        [PreserveSig] int CopyItems([MarshalAs(UnmanagedType.Interface)] object items, IShellItem folder);
        [PreserveSig] int DeleteItem(IShellItem item, IFileOperationProgressSink sink);
        [PreserveSig] int DeleteItems([MarshalAs(UnmanagedType.Interface)] object items);
        [PreserveSig] int NewItem(IShellItem folder, uint attributes, [MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.LPWStr)] string template, IFileOperationProgressSink sink);
        [PreserveSig] int PerformOperations();
        [PreserveSig] int GetAnyOperationsAborted([MarshalAs(UnmanagedType.Bool)] out bool aborted);
    }
}
