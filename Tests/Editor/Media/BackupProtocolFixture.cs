using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Cysharp.Threading.Tasks;
using Game.Media;
using Game.Media.Backup;
using UnityEngine;

namespace UIFrame.Regression
{
    // Transport fixture only: all client state transitions use the production ABI.
    internal class BackupProtocolFixture : HttpMessageHandler
    {
        [Serializable] internal sealed class Item {
            public string clientTaskId,sourceNamespace,sourceId,sourceVersion,sha256,name,mimeType;
            public long attemptGeneration,byteCount;
        }
        [Serializable] sealed class Request { public int protocolVersion;public string requestId;public Item[] items; }
        [Serializable] sealed class Limits { public int maxItems=32,maxRequestBytes=262144,maxResponseBytes=524288,maxDescriptorBytes=8192;public long maxFileBytes=536870912; }
        [Serializable] sealed class UploadResult { public string clientTaskId,status="UploadRequired";public long attemptGeneration;public BackupUploadDescriptor upload; }
        [Serializable] sealed class Receipt { public string clientTaskId,backupId,sourceNamespace,sourceId,sourceVersion,sha256;public long attemptGeneration,byteCount,confirmedAt; }
        [Serializable] sealed class Confirmed { public string clientTaskId,status="Confirmed";public long attemptGeneration;public Receipt receipt; }
        [Serializable] sealed class StateResult { public string clientTaskId,status;public long attemptGeneration; }
        internal readonly Dictionary<string,Item> Items=new Dictionary<string,Item>();
        internal readonly HashSet<string> Uploaded=new HashSet<string>(),Canceled=new HashSet<string>();
        internal readonly List<int> PlanSizes=new List<int>(),QuerySizes=new List<int>();
        internal int Uploads,Plans,Queries,Cancels,Downloads;
        internal string Account="integration-user",FixedBackupId,ResponseAccount;
        internal bool ConfirmOnPlan,ReverseResults,FailFirstPlan,FailUploads,FailFirstCancel;
        internal Func<Item,CancellationToken,Task> BeforeUpload;
        internal Func<CancellationToken,Task> BeforeQuery;
        internal Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage>> Download;
        internal Func<string,string> RewriteResponse;
        internal Action<Item> BeforeConfirm;
        static string Text(string value)=>JsonUtility.ToJson(new StringValue {value=value}).Substring(9).TrimEnd('}');
        [Serializable] sealed class StringValue {public string value;}
        static long Milliseconds=>DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {
            if(request.Method==HttpMethod.Get) {Downloads++;return await Download(request,token);}
            if(request.Method==HttpMethod.Put) {
                if(request.Headers.Authorization!=null)throw new InvalidOperationException("Business credential leaked to storage");
                if(request.RequestUri.Host!="storage.invalid")throw new InvalidOperationException("Expected separate storage origin");
                string id=request.RequestUri.Segments.Last();var item=Items[id];Uploads++;
                if(BeforeUpload!=null)await BeforeUpload(item,token);
                if(FailUploads)throw new HttpRequestException("Controlled PUT failure");
                var bytes=await request.Content.ReadAsByteArrayAsync();
                if(bytes.LongLength!=item.byteCount)throw new InvalidOperationException("Unexpected upload length");
                Uploaded.Add(id);return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
            if(request.Headers.Authorization==null)throw new InvalidOperationException("Missing business authentication");
            var input=JsonUtility.FromJson<Request>(await request.Content.ReadAsStringAsync());token.ThrowIfCancellationRequested();
            bool plan=request.RequestUri.AbsolutePath.EndsWith("/plans",StringComparison.Ordinal);
            bool cancel=request.RequestUri.AbsolutePath.EndsWith("/cancellations",StringComparison.Ordinal);
            if(plan){Plans++;PlanSizes.Add(input.items.Length);foreach(var item in input.items)Items[item.clientTaskId]=item;}
            else if(cancel)Cancels++;else {Queries++;QuerySizes.Add(input.items.Length);}
            if(!plan && !cancel && BeforeQuery!=null)await BeforeQuery(token);
            if(plan && FailFirstPlan && Plans==1)throw new HttpRequestException("Plan committed but response lost");
            if(cancel && FailFirstCancel && Cancels==1)throw new HttpRequestException("Cancel failed before server arbitration");
            var output=new List<string>();
            foreach(var requested in input.items) {
                string id=requested.clientTaskId;Items.TryGetValue(id,out var item);
                if(cancel && !Uploaded.Contains(id))Canceled.Add(id);
                if(Canceled.Contains(id))output.Add(JsonUtility.ToJson(new StateResult {clientTaskId=id,attemptGeneration=requested.attemptGeneration,status="Canceled"}));
                else if(item!=null && (Uploaded.Contains(id) || ConfirmOnPlan)) {
                    BeforeConfirm?.Invoke(item);
                    output.Add(JsonUtility.ToJson(new Confirmed {clientTaskId=id,attemptGeneration=requested.attemptGeneration,receipt=new Receipt {
                        clientTaskId=id,attemptGeneration=requested.attemptGeneration,backupId=FixedBackupId??id,
                        sourceNamespace=item.sourceNamespace,sourceId=item.sourceId,sourceVersion=item.sourceVersion,sha256=item.sha256,byteCount=item.byteCount,confirmedAt=Milliseconds }}));
                } else if(item!=null)output.Add(JsonUtility.ToJson(new UploadResult {clientTaskId=id,attemptGeneration=requested.attemptGeneration,upload=new BackupUploadDescriptor {
                    clientTaskId=id,attemptGeneration=requested.attemptGeneration,uploadId=id,sha256=item.sha256,byteCount=item.byteCount,url="https://storage.invalid/"+id,
                    headers=new[]{new BackupUploadHeader {name="X-Upload-Authorization",value="limited-upload-token"}},
                    expiresAt=Milliseconds+3600000,successStatusCodes=new[]{200,201,204}}}));
                else output.Add(JsonUtility.ToJson(new StateResult {clientTaskId=id,attemptGeneration=requested.attemptGeneration,status="Absent"}));
            }
            if(ReverseResults)output.Reverse();
            string response="{\"protocolVersion\":2,\"requestId\":"+Text(input.requestId)+",\"account\":"+Text(ResponseAccount??Account)+",\"serverTime\":"+Milliseconds+
                ",\"limits\":"+JsonUtility.ToJson(new Limits())+",\"items\":["+string.Join(",",output)+"]}";
            return new HttpResponseMessage(HttpStatusCode.OK) {Content=new StringContent(RewriteResponse==null?response:RewriteResponse(response))};
        }
    }
    internal static class BackupTestSubmission
    {
        internal static async UniTask<BackupSubmissionResult> SubmitAndWaitAsync(this ImageBackupService service,string id,IReadOnlyList<ImageReference> images,CancellationToken token=default)
        {
            var submission=await service.SubmitAsync(id,images,token);
            return await submission.WaitAsync(token);
        }
        internal static async UniTask<IReadOnlyList<string>> SubmitPhotosAsync(this ImageBackupService service,IReadOnlyList<ImageReference> images,CancellationToken token=default)
            =>(await service.SubmitAndWaitAsync(Guid.NewGuid().ToString("N"),images,token)).AcceptedTaskIds;
    }
}
