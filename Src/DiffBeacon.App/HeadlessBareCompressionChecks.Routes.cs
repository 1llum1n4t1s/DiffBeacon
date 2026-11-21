using System.Security.Cryptography;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DiffBeacon.Providers;

namespace DiffBeacon.App;

internal static partial class HeadlessBareCompressionChecks
{
    private static void CheckRoutesAndWorking(MainWindow window, string folder, Action<Task> pump, Action<string,bool,string> check)
    {
        static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
        var service = new ManagedArchive(); var chain = Path.Combine(folder,"chain.bz2"); var mixed = Path.Combine(folder,"mixed.bz2");
        var route = new ArchiveSource(chain, ["chain", "noname"], containerCompressionPayloadKinds: [CompressionPayloadKind.File,CompressionPayloadKind.File,CompressionPayloadKind.File]);
        var resolved = service.ResolveManifest(route); var bytes = service.ResolveEntry(resolved.Source,"noname",4096);
        File.WriteAllBytes(Path.Combine(folder,"chain-leaf.bin"),bytes);
        check("chain-three-layers", bytes.SequenceEqual("hello\n"u8.ToArray()) && resolved.Source.ContainerCompressionPayloadKinds.SequenceEqual(route.ContainerCompressionPayloadKinds),"File is retained independently at each layer");
        var mixedRoute = new ArchiveSource(mixed,["mixed","inner.bz2"],containerCompressionPayloadKinds:[CompressionPayloadKind.File,CompressionPayloadKind.Tar,CompressionPayloadKind.File]);
        var mixedResolved = service.ResolveManifest(mixedRoute);
        check("mixed-layer-settings", service.ResolveEntry(mixedResolved.Source,"inner",4096).SequenceEqual(bytes),"File/Tar/File yields whole fixed leaf");
        var project = new ComparisonProject { Mode="Archive",LeftReadOnly=true,RightReadOnly=true,
            LeftArchiveInput = new() {RootPath=mixed,RootSha256=Hash(File.ReadAllBytes(mixed)),EntryChain=["mixed","inner.bz2"],ContainerCompressionPayloadKinds=["File","Tar","File"]},
            RightArchiveInput = new() {RootPath=mixed,RootSha256=Hash(File.ReadAllBytes(mixed)),EntryChain=["mixed","inner.bz2"],ContainerCompressionPayloadKinds=["File","Auto","File"]} };
        var retry = new ArchiveSourceRetryDialog(project,[new string?[3],new string?[1],new string?[3]]);
        var retryTask = retry.ShowDialog<string?[][]?>(window);
        Dispatcher.UIThread.RunJobs();
        var retryOwned = ReferenceEquals(retry.Owner, window);
        retry.LeftCompressionPayloadKinds[1].SelectedItem=CompressionPayloadKind.Auto;
        retry.RightCompressionPayloadKinds[1].SelectedItem=CompressionPayloadKind.Tar;
        retry.Retry.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        pump(retryTask);
        var retryAccepted = retryTask.GetAwaiter().GetResult() is not null;
        retry.ApplyNameChoices(project); retry.ClearPasswords();
        check("source-retry-owned-button", retryOwned && retryAccepted, "owned dialog real Retry button completed before comparison adopted layer settings");
        check("source-retry-independent-layers",project.LeftArchiveInput!.ContainerCompressionPayloadKinds!.SequenceEqual(new[]{"File","Auto","File"})
            && project.RightArchiveInput!.ContainerCompressionPayloadKinds!.SequenceEqual(new[]{"File","Tar","File"}),"separate actual layer pickers update only selected side and layer");
        // 型付き子入力は専用windowへ採用し、呼出し元paneへDTOを残さない。
        var childWindow = new MainWindow(null, new ImageApplicationOptionsStore(Path.Combine(folder,"child-options.json"))) { Width=1000, Height=700 };
        childWindow.Show(window); Dispatcher.UIThread.RunJobs();
        try
        {
        var active=childWindow.ActivePane;
        active.DiscardChanges();active.ApplyProject(project);pump(active.ComparePathsAsync());Dispatcher.UIThread.RunJobs();
        var childPanel=Panel(active);var confirmed=active.CaptureProject();
        check("child-gui-settings",confirmed.LeftArchiveInput!.ContainerCompressionPayloadKinds!.SequenceEqual(new[]{"File","Auto","File"})
            && confirmed.RightArchiveInput!.ContainerCompressionPayloadKinds!.SequenceEqual(new[]{"File","Tar","File"}),"actual typed child comparison preserves all side/layer choices");
        Task? childRefresh=null;childPanel.ButtonTaskObserved=(_,task)=>childRefresh=task;
        childPanel.RightCompressionPayloadKind.SelectedItem=CompressionPayloadKind.Auto;
        try{childPanel.GetVisualDescendants().OfType<Button>().Single(button=>Equals(button.Content,"アーカイブを再比較")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if(childRefresh is null)throw new InvalidDataException("child refresh Task not observed");pump(childRefresh);}
        finally{childPanel.ButtonTaskObserved=null;}
        check("child-gui-recompare",childPanel.ConfirmedRight.ContainerCompressionPayloadKinds.SequenceEqual(new[]{CompressionPayloadKind.File,CompressionPayloadKind.Tar,CompressionPayloadKind.Auto})
            && childPanel.ConfirmedLeft.ContainerCompressionPayloadKinds.SequenceEqual(new[]{CompressionPayloadKind.File,CompressionPayloadKind.Auto,CompressionPayloadKind.File}),"real child refresh changes only deepest right layer");
        var child=route.WithChild("child.bz2");
        check("new-child-auto",child.ContainerCompressionPayloadKinds.SequenceEqual(new[]{CompressionPayloadKind.File,CompressionPayloadKind.File,CompressionPayloadKind.File,CompressionPayloadKind.Auto}),"new addressed layer starts Auto");
        // 同じroot/chain/leafのAutoとFileだけを変え、revisionと保存byteの混線を検出する。
        var root=Path.Combine(folder,"short.bz2"); var rootHash=Hash(File.ReadAllBytes(root));
        var origin=new ArchiveProjectInput { RootPath=root,RootSha256=rootHash,LeafEntry="short" };
        var store=new ArchiveWorkingStore(); var autoBytes="auto working\n"u8.ToArray(); var fileBytes="file working\n"u8.ToArray();
        ArchiveWorkingSnapshot Snapshot(byte[] payload,string[]? kinds,string[]? entries=null,string leaf="short") => new()
        { EntryChain=entries??[],LeafEntry=leaf,ContainerCompressionPayloadKinds=kinds,Bytes=payload,Sha256=Hash(payload),EncodingName="utf-8" };
        store.Save(origin,0,Snapshot(autoBytes,null)); var autoRevision=store.Revision(origin);
        var fileOrigin=origin with { ContainerCompressionPayloadKinds=["File"] };
        check("working-auto-alias",store.Revision(origin with {ContainerCompressionPayloadKinds=["Auto"]})==autoRevision,"allAuto and absent share legacy identity");
        check("working-file-no-auto",store.Revision(fileOrigin)==0 && store.Find(new ArchiveSource(root,rootSha256:rootHash,containerCompressionPayloadKinds:[CompressionPayloadKind.File]),"short") is null,"new mode does not inherit unrelated saved bytes");
        store.Save(fileOrigin,0,Snapshot(fileBytes,["File"])); var fileRevision=store.Revision(fileOrigin);
        var revisionConflict=false; try {store.Save(fileOrigin,autoRevision,Snapshot("wrong"u8.ToArray(),["File"]));} catch(InvalidOperationException){revisionConflict=true;}
        check("working-revision-conflict",revisionConflict && store.Revision(fileOrigin)==fileRevision,"old sibling revision cannot replace selected working bytes");
        var actualAuto=store.Find(new ArchiveSource(root,rootSha256:rootHash),"short")!;
        var actualFile=store.Find(new ArchiveSource(root,rootSha256:rootHash,containerCompressionPayloadKinds:[CompressionPayloadKind.File]),"short")!;
        check("working-separate-bytes",actualAuto.Bytes!.SequenceEqual(autoBytes) && actualFile.Bytes!.SequenceEqual(fileBytes) && autoRevision!=fileRevision,"full stored bytes and revisions stay separate");
        File.WriteAllBytes(Path.Combine(folder,"working-auto.bin"),actualAuto.Bytes!);File.WriteAllBytes(Path.Combine(folder,"working-file.bin"),actualFile.Bytes!);
        var ancestor=new ArchiveProjectInput { RootPath=mixed,RootSha256=Hash(File.ReadAllBytes(mixed)),ContainerCompressionPayloadKinds=["File"] };
        var descendant=ancestor with {EntryChain=["mixed","inner.bz2"],LeafEntry="inner",ContainerCompressionPayloadKinds=["File","Tar","File"]};
        store.Save(descendant,0,Snapshot(fileBytes,["File","Tar","File"],["mixed","inner.bz2"],"inner"));
        var autoDescendant=descendant with {ContainerCompressionPayloadKinds=["File","Tar","Auto"]};
        store.Save(autoDescendant,0,Snapshot(autoBytes,["File","Tar","Auto"],["mixed","inner.bz2"],"inner"));
        childPanel.SetWorkingDocuments(store);pump(childPanel.PreviewAsync(childPanel.Rows.Single()));
        check("child-gui-working-modes",childPanel.Rows.Single().Status!="Equal" && childPanel.PreviewText.Contains("61 75 74 6F 20 77 6F 72 6B 69 6E 67 0A",StringComparison.Ordinal),"shared store projects only exact ancestor/descendant mode to right GUI preview");
        var captured=store.Capture(ancestor);
        check("working-prefix-ancestor",captured.WorkingDocuments?.Length==2 && captured.WorkingDocuments.Any(copy=>copy.Bytes!.SequenceEqual(fileBytes)) && captured.WorkingDocuments.Any(copy=>copy.Bytes!.SequenceEqual(autoBytes))
            && store.Capture(ancestor with {ContainerCompressionPayloadKinds=["Auto"]}).WorkingDocuments is null,"nonAuto descendant marker participates only inside addressed prefix");
        var legacyProject=new ComparisonProject { Mode="Archive",LeftReadOnly=true,RightReadOnly=true,
            LeftArchiveInput=new(){RootPath=root,RootSha256=rootHash,ContainerGZipPayloadKinds=["File"]},
            RightArchiveInput=new(){RootPath=root,RootSha256=rootHash,ContainerGZipPayloadKinds=["File"]} };
        var legacyWorkspace=WorkspaceStore.SerializeWorkspace(new(){Entries=[legacyProject]});
        File.WriteAllBytes(Path.Combine(folder,"legacy-gzip-v8.json"),legacyWorkspace);
        using(var legacy=JsonDocument.Parse(legacyWorkspace))check("legacy-gzip-v8",legacy.RootElement.GetProperty("formatVersion").GetInt32()==8
            && !legacy.RootElement.GetRawText().Contains("containerCompressionPayloadKinds",StringComparison.Ordinal),"old gzip schema omits new allAuto column");
        var legacyKeys=ArchivePayloadSettings.NormalizedChoices(null,null,1).ToArray();
        check("identity-auto-legacy",legacyKeys.SequenceEqual(ArchivePayloadSettings.NormalizedChoices(null,null,1,compressionPayloadKinds:["Auto"]))
            && legacyKeys.SequenceEqual(ArchivePayloadSettings.NormalizedChoices(null,null,3,1,compressionPayloadKinds:["Auto","Tar","File"])),"nonAuto outside prefix does not change old key");
        // GUIの確定設定を持ったpanelへ共有storeを適用し、modeが異なる保存版をoverlayしない。
        ArchivePanel? workingPanel=null;
        async Task MakePanel()=>workingPanel=await ArchivePanel.CreateForSourcesAsync(new(root,rootSha256:rootHash,containerCompressionPayloadKinds:[CompressionPayloadKind.File]),new(root,rootSha256:rootHash),CancellationToken.None,guardOutput:null,leftAncestors:[],rightAncestors:[]);
        pump(MakePanel()); workingPanel!.SetWorkingDocuments(store);
        check("working-panel-mode-overlay",workingPanel.Rows.Single().Status!="Equal","same physical root has separate File/Auto saved content");
        pump(workingPanel.PreviewAsync(workingPanel.Rows.Single()));
        check("working-panel-preview",workingPanel.PreviewText.Contains("66 69 6C 65 20 77 6F 72 6B 69 6E 67 0A",StringComparison.Ordinal),"GUI preview observes only selected File save");
        workingPanel.Dispose();
        CheckWorkingSaveButtons(folder, pump, check);
        using var file=File.Create(Path.Combine(folder,"working-proof.json")); using var writer=new Utf8JsonWriter(file,new JsonWriterOptions{Indented=true});
        writer.WriteStartObject();writer.WriteString("rootSha256",rootHash);writer.WriteNumber("autoRevision",autoRevision);writer.WriteNumber("fileRevision",fileRevision);
        writer.WriteString("autoHex",Convert.ToHexString(actualAuto.Bytes!));writer.WriteString("fileHex",Convert.ToHexString(actualFile.Bytes!));
        writer.WriteString("ancestorRootSha256",ancestor.RootSha256);writer.WriteStartArray("descendantCompressionKinds");foreach(var k in captured.WorkingDocuments![0].ContainerCompressionPayloadKinds!)writer.WriteStringValue(k);writer.WriteEndArray();writer.WriteEndObject();
        }
        finally { childWindow.ActivePane.DiscardChanges(); childWindow.Close(); window.Activate(); }
    }
    // 失敗条件: 新しい本文設定の保存先混線、実ボタンの未完了、原本変更、保存後dirtyの残存。
    private static void CheckWorkingSaveButtons(string folder, Action<Task> pump, Action<string,bool,string> check)
    {
        var root = Path.Combine(folder, "short.bz2"); var binaryRoot = Path.Combine(folder, "short.Z");
        var before = File.ReadAllBytes(root); var binaryBefore = File.ReadAllBytes(binaryRoot);
        var owner = new MainWindow(null, new ImageApplicationOptionsStore(Path.Combine(folder, "save-button-options.json"))) { Width = 1000, Height = 700 };
        owner.Show(); Dispatcher.UIThread.RunJobs();
        try
        {
            ArchiveProjectInput Input(string path, string kind) => new() { RootPath = path, RootSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))), LeafEntry = "short", ContainerCompressionPayloadKinds = [kind], InheritedReadOnly = false };
            var text = owner.ActivePane;
            text.ApplyProject(new() { Mode = "Text", LeftReadOnly = true, RightReadOnly = true, LeftArchiveInput = Input(root,"Auto"), RightArchiveInput = Input(root,"File") });
            pump(text.ComparePathsAsync());
            text.LeftEditor.Text = "GUI auto saved\n"; text.RightEditor.Text = "GUI file saved\n";
            foreach (var label in new[] { "左を保存", "右を保存" })
            {
                Task? saved = null; text.TextSaveTaskObserved = (_,task) => saved = task;
                try
                {
                    text.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content,label)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    if (saved is null) throw new InvalidDataException("Text save button Task not observed");
                    pump(saved);
                }
                finally { text.TextSaveTaskObserved = null; }
            }
            check("button-text-save-clean", !text.HasUnsavedChanges, "real left/right save buttons completed selected working saves");
            pump(text.ComparePathsAsync());
            check("button-text-modes-reload", text.LeftEditor.Text == "GUI auto saved\n" && text.RightEditor.Text == "GUI file saved\n", "same root/leaf Auto and File retain separate saved bytes");
            File.WriteAllBytes(Path.Combine(folder,"button-text-auto.bin"),System.Text.Encoding.UTF8.GetBytes(text.LeftEditor.Text!));
            File.WriteAllBytes(Path.Combine(folder,"button-text-file.bin"),System.Text.Encoding.UTF8.GetBytes(text.RightEditor.Text!));
            var binary = owner.AddSession();
            binary.ApplyProject(new() { Mode="Binary",LeftReadOnly=true,RightReadOnly=true,LeftArchiveInput=Input(binaryRoot,"File"),RightArchiveInput=Input(binaryRoot,"Auto") });
            pump(binary.ComparePathsAsync());
            var panel = binary.GetVisualDescendants().OfType<SpecializedViews.BinaryPanel>().Single();
            var changed = panel.Capture(false).CopyBytes();
            if (changed.Length == 0) throw new InvalidDataException("Binary save fixture is empty");
            changed[0] ^= 0xff;
            panel.LeftHex.Text = string.Join(" ",changed.Select(value => value.ToString("X2",System.Globalization.CultureInfo.InvariantCulture))); panel.Apply(false);
            Task? binarySaved = null; var originalSave = panel.SaveContent ?? throw new InvalidDataException("Binary save action absent");
            panel.SaveContent = (side,path,token) => binarySaved = originalSave(side,path,token);
            try
            {
                panel.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content,"左を保存")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                if (binarySaved is null) throw new InvalidDataException("Binary save button Task not observed");
                pump(binarySaved);
            }
            finally { panel.SaveContent = originalSave; }
            check("button-binary-save-clean", !panel.Dirty(false), "real Binary save button completed with File payload choices");
            pump(binary.ComparePathsAsync());
            var restored = binary.GetVisualDescendants().OfType<SpecializedViews.BinaryPanel>().Single();
            var actual = restored.Capture(false).CopyBytes();
            check("button-binary-modes-reload", actual.SequenceEqual(changed), "recompare reads saved bytes using the same compression identity");
            File.WriteAllBytes(Path.Combine(folder,"button-binary.bin"),actual);
            pump(owner.SaveWorkspaceAsync(Path.Combine(folder,"button-workspace-v9.json")));
            check("button-save-originals", before.SequenceEqual(File.ReadAllBytes(root)) && binaryBefore.SequenceEqual(File.ReadAllBytes(binaryRoot)), "all GUI working saves retain compressed originals");
        }
        finally { owner.Close(); Dispatcher.UIThread.RunJobs(); }
    }
    private static void CheckBrowser(MainWindow window,string folder,Action<Task> pump,Action<string,bool,string> check)
    {
        void Jobs(){Dispatcher.UIThread.RunJobs();AvaloniaHeadlessPlatform.ForceRenderTimerTick();Dispatcher.UIThread.RunJobs();}
        void Click(Button b)=>b.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var caller=window;
        window=new MainWindow(null,new ImageApplicationOptionsStore(Path.Combine(folder,"browser-options.json"))){Width=1000,Height=680};window.Show();Jobs();
        var parent=window.ActivePane;var textLeft=Path.Combine(folder,"browser-parent-left.txt");var textRight=Path.Combine(folder,"browser-parent-right.txt");
        File.WriteAllText(textLeft,"left\n");File.WriteAllText(textRight,"right\n");parent.ApplyProject(new(){Mode="Text",LeftPath=textLeft,RightPath=textRight});pump(parent.ComparePathsAsync());
        parent.TextEditor(0).Text+="pending edit\n";Jobs();var dirtyText=parent.TextEditor(0).Text;var initialTabs=window.SessionPanes.Count();
        check("browser-parent-dirty-before",parent.HasUnsavedChanges,"real Text edit is unsaved before opening selection modal");
        Task<bool>? opening=null; IndependentTextInputDialog? dialog=null;
        window.IndependentTextInputDialogShown=shown=>dialog=shown;
        window.IndependentTextInputOperationObserved=task=>opening=task;
        try
        {
            Click(window.ActivePane.IndependentTextInputsButton);Jobs();
            if(dialog is null || opening is null)throw new InvalidDataException("owned independent input dialog not observed");
            check("browser-owner",ReferenceEquals(dialog.Owner,window),"real entry button opens owned modal");
            var browser=dialog.Side(0);browser.Kind.SelectedIndex=2;browser.RootPath.Text="";browser.RootPath.Text=Path.Combine(folder,"mixed.bz2");
            browser.CompressionPayloadKindPicker.SelectedItem=CompressionPayloadKind.File;
            void TraceBrowser(string stage)
            {
                using var file = new FileStream(Path.Combine(folder,"browser-operations.ndjson"),FileMode.Append,FileAccess.Write,FileShare.Read);
                using var writer = new Utf8JsonWriter(file);
                writer.WriteStartObject();writer.WriteString("stage",stage);writer.WriteString("root",browser.RootPath.Text);
                writer.WriteString("kind",browser.KindName);writer.WriteString("summary",browser.Summary);
                writer.WriteString("compression",browser.CompressionPayloadKindPicker.SelectedItem?.ToString());
                writer.WriteBoolean("openEnabled",browser.OpenContainer.IsEnabled);writer.WriteBoolean("loadEnabled",browser.Load.IsEnabled);
                writer.WriteStartArray("rows");foreach(var row in browser.Entries.ItemsSource?.Cast<object>() ?? [])writer.WriteStringValue(row.ToString());writer.WriteEndArray();
                writer.WriteStartArray("text");foreach(var block in browser.GetVisualDescendants().OfType<TextBlock>())writer.WriteStringValue(block.Text);writer.WriteEndArray();
                writer.WriteEndObject();writer.Flush();file.WriteByte(10);
            }
            void Operate(Button button)
            {
                Jobs();var label=ReferenceEquals(button,browser.OpenContainer)?"open":"load";TraceBrowser("before-"+label);
                if(!button.IsEnabled)throw new InvalidDataException("browser "+label+" button is disabled; see browser-operations.ndjson");
                Task? operation=null;browser.OperationTaskObserved=(_,t)=>operation=t;
                try { Click(button);if(operation is null)throw new InvalidDataException("browser button Task not observed");pump(operation);Jobs();TraceBrowser("after-"+label); }
                finally { browser.OperationTaskObserved=null; }
            }
            void Select(string name)
            {
                var matches=(browser.Entries.ItemsSource?.Cast<object>() ?? []).Where(row=>row.ToString()!.StartsWith(name+" （",StringComparison.Ordinal)).ToArray();
                TraceBrowser("select-"+name);
                if(matches.Length!=1)
                {
                    using var frame=dialog?.CaptureRenderedFrame();
                    if(frame is not null){using var png=File.Create(Path.Combine(folder,"browser-selection-failure.png"));frame.Save(png,new Avalonia.Media.Imaging.PngBitmapEncoderOptions());}
                    throw new InvalidDataException("browser selection "+name+": expected one matching row, actual="+matches.Length+"; see browser-operations.ndjson");
                }
                browser.Entries.SelectedItem=matches[0];Jobs();
            }
            Operate(browser.Load);Select("mixed");Operate(browser.OpenContainer);
            browser.CompressionPayloadKindPicker.SelectedItem=CompressionPayloadKind.Tar;Operate(browser.Load);Select("inner.bz2");Operate(browser.OpenContainer);
            browser.CompressionPayloadKindPicker.SelectedItem=CompressionPayloadKind.File;Operate(browser.Load);Select("inner");Jobs();
            var captured=browser.Capture();
            check("browser-layer-propagation",captured.Archive!.EntryChain.SequenceEqual(new[]{"mixed","inner.bz2"})
                && captured.Archive.ContainerCompressionPayloadKinds!.SequenceEqual(new[]{"File","Tar","File"}) && captured.Archive.LeafEntry=="inner" && captured.ReadOnly,"real Load/Open/selection at all addressed layers");
            var right=dialog.Side(2);right.Kind.SelectedIndex=2;right.RootPath.Text=Path.Combine(folder,"short.Z");right.CompressionPayloadKindPicker.SelectedItem=CompressionPayloadKind.File;
            Task? rightOperation=null;right.OperationTaskObserved=(_,t)=>rightOperation=t;Click(right.Load);if(rightOperation is null)throw new InvalidDataException("right Load Task not observed");pump(rightOperation);Jobs();
            right.Entries.SelectedItem=right.Entries.ItemsSource!.Cast<object>().Single();Jobs();
            check("browser-sides-independent",right.Capture().Archive!.ContainerCompressionPayloadKinds!.SequenceEqual(new[]{"File"})
                && browser.Capture().Archive!.ContainerCompressionPayloadKinds!.SequenceEqual(new[]{"File","Tar","File"}),"right selection cannot replace left layer choices");
            foreach(var (width,height) in new[]{(1000d,680d),(850d,550d)})
            {dialog.Width=width;dialog.Height=height;Jobs();using var layoutFile=File.Create(Path.Combine(folder,"browser-bounds-"+width+".json"));using var layout=new Utf8JsonWriter(layoutFile,new JsonWriterOptions{Indented=true});layout.WriteStartObject();layout.WriteNumber("width",width);layout.WriteNumber("height",height);layout.WriteStartArray("controls");foreach(var control in new Control[]{browser.RootPath,browser.CompressionPayloadKindPicker,browser.Load,browser.Entries,dialog.Compare})
                {control.BringIntoView();Jobs();var p=control.TranslatePoint(default,dialog);check("browser-layout-"+width+"-"+control.GetType().Name,p is {} point && point.Y>=0 && point.Y+control.Bounds.Height<=height
                    && (control!=browser.Entries || control.Bounds.Height>=100),"scroll reach input/picker/Load/list/Compare");layout.WriteStartObject();layout.WriteString("type",control.GetType().Name);layout.WriteNumber("x",p?.X??-1);layout.WriteNumber("y",p?.Y??-1);layout.WriteNumber("width",control.Bounds.Width);layout.WriteNumber("height",control.Bounds.Height);layout.WriteBoolean("list",control==browser.Entries);layout.WriteEndObject();}layout.WriteEndArray();layout.WriteEndObject();using var frame=dialog.CaptureRenderedFrame()??throw new InvalidDataException("browser frame absent");using var png=File.Create(Path.Combine(folder,"browser-"+width+".png"));frame.Save(png,new Avalonia.Media.Imaging.PngBitmapEncoderOptions());}
            using var proof=File.Create(Path.Combine(folder,"browser-proof.json"));using var writer=new Utf8JsonWriter(proof,new JsonWriterOptions{Indented=true});writer.WriteStartObject();writer.WriteString("root",captured.Archive!.RootPath);writer.WriteString("leaf",captured.Archive.LeafEntry);
            writer.WriteStartArray("entryChain");foreach(var s in captured.Archive.EntryChain)writer.WriteStringValue(s);writer.WriteEndArray();writer.WriteStartArray("compressionKinds");foreach(var s in captured.Archive.ContainerCompressionPayloadKinds!)writer.WriteStringValue(s);writer.WriteEndArray();writer.WriteBoolean("readOnly",captured.ReadOnly);writer.WriteString("parentDirtyTextBefore",dirtyText);writer.WriteString("parentDirtyTextAfter",parent.TextEditor(0).Text);writer.WriteBoolean("parentDirtyBeforeCancel",parent.HasUnsavedChanges);writer.WriteEndObject();
            Click(dialog.Cancel);pump(opening);Jobs();check("browser-cancel-modal",!dialog.IsVisible,"cancel actual owned modal and collect original opener Task");
            check("browser-parent-dirty-retained",parent.HasUnsavedChanges && parent.TextEditor(0).Text==dirtyText && window.SessionPanes.Count()==initialTabs && ReferenceEquals(window.ActivePane,parent),"owned browser cancellation preserves dirty Text and original tab");
        }
        finally {window.IndependentTextInputDialogShown=null;window.IndependentTextInputOperationObserved=null;if(dialog?.IsVisible==true)dialog.Close();parent.DiscardChanges();window.Close();caller.Activate();}
    }
}
