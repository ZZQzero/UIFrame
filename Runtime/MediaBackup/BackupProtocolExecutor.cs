using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Cysharp.Threading.Tasks;
using UnityEngine;
using Command = Game.Media.Backup.BackupRepository.Command;

namespace Game.Media.Backup
{
    public sealed partial class ImageBackupService
    {
        // Desktop ownership is process-bound. Persist only opaque references;
        // mobile adapters use Keychain/AndroidKeyStore independently of this map.
        readonly Dictionary<string,string> desktopCredentials=new Dictionary<string,string>(StringComparer.Ordinal);
        readonly Dictionary<string,DesktopWork> desktopUploads=new Dictionary<string,DesktopWork>(StringComparer.Ordinal);
        readonly SemaphoreSlim desktopSignal=new SemaphoreSlim(0,1);
        DesktopWork desktopControl;
        Exception executorFailure;
        Func<bool> desktopNetworkPolicy;
        sealed class DesktopWork : IDisposable
        {
            internal readonly string Id;
            internal readonly CancellationTokenSource Cancellation;
            internal Task Completion;
            internal bool Pausing;
            internal int ControlKind=-1;
            internal long ControlDeadline;
            internal DesktopWork(string id,CancellationToken lifetime) {Id=id;Cancellation=CancellationTokenSource.CreateLinkedTokenSource(lifetime);}
            public void Dispose()=>Cancellation.Dispose();
        }
        string StoreDesktopCredential(string value)
        {
            string reference=Guid.NewGuid().ToString("N");desktopCredentials.Add(reference,value);return reference;
        }
        string DesktopCredential(string reference)
        {
            if(reference==null || !desktopCredentials.TryGetValue(reference,out var value))throw new InvalidOperationException("Desktop execution credentials are unavailable; explicitly reconcile with current authentication.");
            return value;
        }
        void WakeDesktop() {if(desktopSignal.CurrentCount==0)desktopSignal.Release();}
        void StartDesktopDriver()
        {
            if(nativeEnabled)return;
            if(executorFailure!=null)System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(executorFailure).Throw();
            WakeDesktop();if(running)return;
            running=true;DriveDesktop().Forget(error=>Debug.LogException(error));
        }
        internal void SetDesktopNetworkPolicy(Func<bool> policy)
        {
            if(nativeEnabled && policy!=null)throw new InvalidOperationException("A C# callback cannot enforce native background network constraints.");
            desktopNetworkPolicy=policy;
        }
        public async UniTask WaitForIdleAsync(CancellationToken cancellationToken=default)
        {
            using var operation=EnterOperation();
            if(nativeEnabled)throw new InvalidOperationException("Use task queries to observe the operating system's background queue.");
            while(running || preparing)await UniTask.Delay(100,ignoreTimeScale:true,cancellationToken:cancellationToken);
            if(preparationFailure!=null)System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(preparationFailure).Throw();
            if(executorFailure!=null)System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(executorFailure).Throw();
        }
        async UniTask DriveDesktop()
        {
            Exception primary=null;
            try
            {
                for(;;)
                {
                    lifetime.Token.ThrowIfCancellationRequested();
                    if(desktopControl?.Completion.IsCompleted==true){await desktopControl.Completion;desktopControl.Dispose();desktopControl=null;}
                    foreach(var work in desktopUploads.Values.Where(x=>x.Completion.IsCompleted).ToArray())
                    {await work.Completion;desktopUploads.Remove(work.Id);work.Dispose();}
                    await Db(Command.ProtocolActions,default,0,Now);
                    await ReleaseDesktopResources();
                    await CleanupFilesAsync(default,32);
                    var controls=(await Db(Command.Controls,default,0,"")).Rows;
                    foreach(var control in controls.Where(x=>ControlExpired(x)))
                    {
                        if(desktopControl?.Id==control.Text("id"))desktopControl.Cancellation.Cancel();
                        else {
                            await Db(Command.ControlFail,default,control.Text("id"),"Confirmation deadline reached",Now,false);
                            await Db(Command.ControlRelease,default,control.Text("id"));
                        }
                    }
                    bool permitted=desktopNetworkPolicy==null || desktopNetworkPolicy();
                    if(!permitted)
                    {
                        foreach(var upload in desktopUploads.Values)upload.Cancellation.Cancel();
                        desktopControl?.Cancellation.Cancel();
                    }
                    else
                    {
                        if(desktopControl==null)
                        {
                            var ready=controls.FirstOrDefault(x=>x.Number("not_before_utc")<=Now && x.Number("state")<=1 && !ControlExpired(x));
                            if(ready==null)
                            {
                                var created=await Db(Command.ControlCreate,default,Guid.NewGuid().ToString("N"),0,Now,false,!preparing);
                                if(created.Rows.Count!=0 && created.Single.Number("not_before_utc")<=Now)ready=created.Single;
                            }
                            if(ready!=null)
                            {
                                desktopControl=new DesktopWork(ready.Text("id"),lifetime.Token) {ControlKind=(int)ready.Number("kind"),ControlDeadline=ready.Number("deadline_utc")};
                                desktopControl.Completion=RunDesktopControl(ready,desktopControl).AsTask();
                            }
                        }
                        if(desktopUploads.Count<2)
                        {
                            var uploads=(await Db(Command.Uploads,default,0,"")).Rows;
                            foreach(var row in uploads)
                            {
                                if(desktopUploads.Count==2)break;
                                string id=row.Text("task_id");
                                if(desktopUploads.ContainsKey(id) || row.Number("execution_state")!=0 || row.Number("upload_expires_utc")<=Now)continue;
                                var work=new DesktopWork(id,lifetime.Token);desktopUploads.Add(id,work);
                                work.Completion=RunDesktopUpload(row,work).AsTask();
                            }
                        }
                    }
                    controls=(await Db(Command.Controls,default,0,"")).Rows;
                    bool pending=(await Db(Command.Attempts,default,0,0,1)).Rows.Count!=0;
                    if(desktopControl==null && desktopUploads.Count==0 && controls.Count==0 && !pending)break;
                    // A signal wakes the loop on submission or network completion.
                    // Future confirmation work sleeps without repeated DB polling.
                    long next=(await Db(Command.ProtocolWake,default,0)).Single.Number("next_utc");
                    long future=controls.Where(x=>x.Number("not_before_utc")>Now).Select(x=>x.Number("not_before_utc")).Append(next>Now?next:Now+TimeSpan.TicksPerSecond*5).Min();
                    int delay=(int)Math.Max(1,Math.Min(permitted?60000:5000,(future-Now)/TimeSpan.TicksPerMillisecond));
                    await desktopSignal.WaitAsync(delay,lifetime.Token);
                }
            }
            catch(OperationCanceledException) when(lifetime.IsCancellationRequested) { }
            catch(Exception error){primary=error;executorFailure=error;}
            finally
            {
                var work=desktopUploads.Values.ToList();if(desktopControl!=null)work.Add(desktopControl);
                foreach(var item in work)try{item.Cancellation.Cancel();}catch(Exception error){if(primary==null)primary=error;else Debug.LogException(error);}
                foreach(var item in work)
                {
                    try {await item.Completion;}catch(Exception error){if(primary==null)primary=error;else Debug.LogException(error);}
                    item.Dispose();
                }
                desktopUploads.Clear();desktopControl=null;if(primary!=null)executorFailure=primary;running=false;
            }
            if(primary!=null)System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(primary).Throw();
        }

        static bool ControlExpired(BackupRepository.Row row)=>row.Number("deadline_utc")>0 && row.Number("deadline_utc")<=Now;

        async UniTask RunDesktopControl(BackupRepository.Row control,DesktopWork work)
        {
            string id=work.Id;bool determined=false;var provisional=new List<string>();Exception primary=null;
            try
            {
                if(work.ControlDeadline>0) {
                    long remaining=work.ControlDeadline-Now;
                    if(remaining<=0)throw new TimeoutException("Confirmation deadline reached");
                    work.Cancellation.CancelAfter(TimeSpan.FromTicks(remaining));
                }
                await Db(Command.ControlSeal,work.Cancellation.Token,id);
                if(!(await Db(Command.ControlSubmitted,work.Cancellation.Token,id,"desktop:"+id)).Single.Flag("admitted") ||
                    !(await Db(Command.ControlStart,work.Cancellation.Token,id)).Single.Flag("admitted")) {
                    await Db(Command.ControlFail,default,id,"Admission closed before transfer",Now,true);determined=true;return;
                }
                string response=await SendControlFile(control,work.Cancellation.Token);
                var descriptors=(await Db(Command.ControlValidate,default,id,response)).Rows;
                var values=new List<object>{id,response,Now,descriptors.Count};
                foreach(var row in descriptors)
                {
                    string reference=StoreDesktopCredential(row.Text("descriptor"));provisional.Add(reference);
                    values.Add(row.Text("task_id"));values.Add(reference);
                }
                await Db(Command.ControlApply,default,values.ToArray());determined=true;
            }
            catch(Exception error)
            {
                primary=error;
                if(error is BackupRepositoryException database && database.Code!=1)throw;
                bool expired=work.ControlDeadline>0 && work.ControlDeadline<=Now;
                try {await Db(Command.ControlFail,default,id,expired?"Confirmation deadline reached":TransferError(error),Now,work.Pausing && !expired);determined=true;}
                catch(Exception persistence){Debug.LogException(persistence);System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();throw;}
                // Per-request failures are persisted. Unrelated uploads continue.
            }
            finally
            {
                try
                {
                    if(determined)await Db(Command.ControlRelease,default,id);
                    // References whose application failed before ownership transfer
                    // are discarded; unknown DB outcomes keep their references.
                    if(provisional.Count!=0 && determined)
                    {
                        var rows=(await Db(Command.Attempts,default,0,0,100)).Rows;
                        var owned=new HashSet<string>(rows.Select(x=>x.Text("upload_reference")),StringComparer.Ordinal);
                        foreach(string reference in provisional)if(!owned.Contains(reference))desktopCredentials.Remove(reference);
                    }
                }
                catch(Exception cleanup){if(primary==null)throw;Debug.LogException(cleanup);System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(primary).Throw();}
                WakeDesktop();
            }
        }
        async UniTask<string> SendControlFile(BackupRepository.Row control,CancellationToken token)
        {
            string[] paths={"/v2/backup/plans","/v2/backup/status","/v2/backup/cancellations"};
            using var file=File.OpenRead(Path.Combine(root,control.Text("relative_path")));
            using var request=new HttpRequestMessage(HttpMethod.Post,server+paths[(int)control.Number("kind")]);
            request.Headers.Authorization=new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer",DesktopCredential(control.Text("credential_reference")));
            request.Content=new StreamContent(file,16*1024);request.Content.Headers.ContentType=new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            return await WithResponse(request,async (response,deadline)=>
            {
                CheckResponse(response);
                if((int)response.StatusCode!=200)throw new InvalidDataException("Control request requires HTTP 200.");
                return await ReadResponse(response,deadline);
            },token);
        }
        async UniTask RunDesktopUpload(BackupRepository.Row task,DesktopWork work)
        {
            string id=work.Id;long generation=task.Number("generation");bool started=false,ended=false;Exception primary=null;
            try
            {
                var descriptor=JsonUtility.FromJson<BackupUploadDescriptor>(DesktopCredential(task.Text("upload_reference")));
                started=(await Db(Command.UploadStart,work.Cancellation.Token,id,generation,"desktop:"+id,Now)).Single.Flag("started");
                if(!started)return;
                await SendPhotoFile(task,descriptor,work.Cancellation.Token);
                await Db(Command.UploadEnd,default,id,generation,1,"",Now);ended=true;
            }
            catch(Exception error)
            {
                primary=error;
                if(error is BackupRepositoryException)throw;
                if(!started && error is OperationCanceledException && work.Cancellation.IsCancellationRequested)return;
                if(started)
                {
                    try {await Db(Command.UploadEnd,default,id,generation,work.Pausing?2:0,TransferError(error),Now);ended=true;}
                    catch(Exception persistence){Debug.LogException(persistence);System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();}
                }
                else
                {
                    try {await Db(Command.UploadReject,default,id,generation,TransferError(error),Now);ended=true;}
                    catch(Exception persistence){Debug.LogException(persistence);System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();}
                }
            }
            finally
            {
                if(ended)
                {
                    desktopCredentials.Remove(task.Text("upload_reference"));
                    try {await Db(Command.UploadRelease,default,id,generation,Now);}
                    catch(Exception cleanup){if(primary==null)throw;Debug.LogException(cleanup);System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(primary).Throw();}
                }
                WakeDesktop();
            }
        }
        async UniTask SendPhotoFile(BackupRepository.Row task,BackupUploadDescriptor descriptor,CancellationToken token)
        {
            using var file=File.OpenRead(Path.Combine(root,task.Text("relative_path")));
            if(file.Length!=task.Number("byte_count"))throw new IOException("Prepared photo size changed.");
            using var request=new HttpRequestMessage(HttpMethod.Put,descriptor.url);
            request.Content=new StreamContent(file,128*1024);
            foreach(var header in descriptor.headers)
            {
                bool added=header.name.StartsWith("Content-",StringComparison.OrdinalIgnoreCase)
                    ?request.Content.Headers.TryAddWithoutValidation(header.name,header.value):request.Headers.TryAddWithoutValidation(header.name,header.value);
                if(!added)throw new InvalidDataException("Upload descriptor contains an unsupported HTTP header.");
            }
            await WithResponse(request,(response,deadline)=>
            {
                if(!descriptor.successStatusCodes.Contains((int)response.StatusCode))throw new BackupHttpException((int)response.StatusCode,"Storage PUT did not return an allowed success status.",null);
                return UniTask.FromResult(true);
            },token);
        }
        async UniTask ReleaseDesktopResources()
        {
            var rows=(await Db(Command.Attempts,default,0,0,100)).Rows;
            foreach(var row in rows)
            {
                string id=row.Text("id");long generation=row.Number("current_generation");
                if(desktopUploads.ContainsKey(id) || !string.IsNullOrEmpty(row.Text("control_id")))continue;
                bool stopped=row.Number("protocol_phase")==3 || row.Number("state")==4;
                bool expired=row.Number("protocol_phase")==1 && row.Number("upload_expires_utc")<=Now;
                string descriptor=row.Text("upload_reference");
                if((stopped || expired || row.Number("protocol_phase")==2) && !string.IsNullOrEmpty(descriptor))
                {
                    desktopCredentials.Remove(descriptor);await Db(Command.UploadRelease,default,id,generation,Now);
                }
                if(stopped && (row.Number("protocol_phase")==3 || row.Number("desired_action")!=2))
                {
                    string reference=row.Text("credential_reference");if(reference!=null)desktopCredentials.Remove(reference);
                    await Db(Command.ProtocolRelease,default,id,generation);
                }
            }
            var cleanup=(await Db(Command.ProtocolCleanup,default,Now,32)).Single;
            if(cleanup.Number("failed")!=0)Debug.LogError("Backup control file cleanup failed; its recorded item requires explicit cleanup retry.");
        }
        // HttpClient transport exceptions can include the signed request URL.
        static string TransferError(Exception error)=>error is HttpRequestException
            ?"HTTP transport failed ("+error.GetType().Name+"). Reconcile before retrying."
            :error.Message;
    }
}
