using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UIFrame.Sqlite;

namespace Game.Media.Storage
{
    internal sealed class LibraryRepository
    {
        readonly SqliteDatabase database;
        internal string Id { get; private set; }
        internal long Generation { get; private set; }
        internal LibraryRepository(SqliteDatabase database) { this.database=database; }
        internal static async Task<LibraryRepository> OpenAsync(string path,CancellationToken token)
        {
            if(string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path)) throw new ArgumentException("Absolute library database path required.");
            Directory.CreateDirectory(Path.GetDirectoryName(path)); bool create=!File.Exists(path);
            var database=await SqliteDatabase.OpenAsync(new SqliteOpenOptions(path,create?SqliteOpenMode.CreateNew:SqliteOpenMode.OpenExistingReadWrite),token).ConfigureAwait(false);
            try
            {
                var repository=new LibraryRepository(database);
                if(create)
                {
                    var commands=LibrarySchema.Commands.Select(x=>new SqliteCommand(x)).ToList();
                    commands.Add(new SqliteCommand("INSERT INTO library_settings(singleton,library_id,index_generation) VALUES(1,?,1)",Guid.NewGuid().ToString("N")));
                    await repository.Commit(commands,token).ConfigureAwait(false);
                }
                var app=await repository.Query(new SqliteCommand("PRAGMA application_id"),r=>r.GetInt64(0),token).ConfigureAwait(false);
                var version=await repository.Query(new SqliteCommand("PRAGMA user_version"),r=>r.GetInt64(0),token).ConfigureAwait(false);
                if(app[0]!=1430667852 || version[0]!=1) throw new InvalidDataException("Unexpected image library identity or schema.");
                var identity=await repository.Query(new SqliteCommand("SELECT library_id,index_generation FROM library_settings WHERE singleton=1"),r=>(id:r.GetString(0),generation:r.GetInt64(1)),token).ConfigureAwait(false);
                if(identity.Count!=1) throw new InvalidDataException("Missing image library identity.");
                repository.Id=identity[0].id; repository.Generation=identity[0].generation;
                return repository;
            }
            catch { try { await database.CloseAsync().ConfigureAwait(false); } catch(Exception cleanup) { UnityEngine.Debug.LogException(cleanup); } throw; }
        }
        internal Task<IReadOnlyList<T>> Query<T>(SqliteCommand command,Func<SqliteRow,T> map,CancellationToken token=default)
            => database.QueryPageAsync(command,new SqliteQueryBudget(),map,token);
        internal async Task Commit(IReadOnlyList<SqliteCommand> commands,CancellationToken token=default)
        { using(await database.ExecuteTransactionAsync(new SqliteBatch(new SqliteQueryBudget(),commands.ToArray()),token).ConfigureAwait(false)) {} }
        internal async Task<LibraryScopeState> Scope(ImageLibraryScope scope,long permission,CancellationToken token)
        {
            await Commit(new[]{
                new SqliteCommand("INSERT INTO library_scopes(id,provider,source_identity,revision,permission_generation,access_state,requires_reconcile) VALUES(?,?,?,1,0,?,1) ON CONFLICT(id) DO NOTHING",scope.Id,scope.Kind.ToString(),scope.Source,permission),
                new SqliteCommand("UPDATE library_scopes SET revision=revision+CASE WHEN access_state<>? THEN 1 ELSE 0 END,requires_reconcile=CASE WHEN access_state<>? THEN 1 ELSE requires_reconcile END,permission_generation=permission_generation+CASE WHEN access_state<>? THEN 1 ELSE 0 END,access_fingerprint=CASE WHEN access_state<>? THEN NULL ELSE access_fingerprint END,access_state=? WHERE id=? AND provider=? AND source_identity=?",permission,permission,permission,permission,permission,scope.Id,scope.Kind.ToString(),scope.Source).ExpectAffectedRows(1)
            },token).ConfigureAwait(false);
            return await State(scope.Id,token).ConfigureAwait(false);
        }
        internal async Task<LibraryScopeState> State(string scope,CancellationToken token=default)
        {
            return await FindState(scope,token).ConfigureAwait(false)??throw new ArgumentException("Library scope not registered.");
        }
        internal Task RevokeAccess(string scope)
            => Commit(new[]{new SqliteCommand("UPDATE library_scopes SET revision=revision+1,permission_generation=permission_generation+1,requires_reconcile=1,access_state=?,access_fingerprint=NULL WHERE id=? AND access_state IN(?,?)",
                (long)LibraryAccess.Denied,scope,(long)LibraryAccess.Authorized,(long)LibraryAccess.Limited)});
        internal async Task<LibraryScopeState> FindState(string scope,CancellationToken token=default)
        {
            var rows=await Query(new SqliteCommand("SELECT id,revision,permission_generation,requires_reconcile,access_state FROM library_scopes WHERE id=?",scope),
                r=>new LibraryScopeState { Scope=r.GetString(0),Revision=r.GetInt64(1),Permission=r.GetInt64(2),RequiresReconcile=r.GetInt64(3)!=0,Access=r.GetInt64(4) },token).ConfigureAwait(false);
            return rows.Count==0?null:rows[0];
        }
        internal async Task<(long sequence,long retained)> Position(CancellationToken token=default)
        {
            var rows=await Query(new SqliteCommand("SELECT max((SELECT coalesce(max(sequence),0) FROM change_log),retained_after_seq),retained_after_seq FROM library_settings WHERE singleton=1"),
                r=>(r.GetInt64(0),r.GetInt64(1)),token).ConfigureAwait(false); return rows[0];
        }
        internal async Task<string> Begin(LibraryScopeState scope,long start,CancellationToken token,string platformBoundary=null)
        {
            string run=Guid.NewGuid().ToString("N");
            await Commit(new[]{
                new SqliteCommand("UPDATE library_scopes SET requires_reconcile=1 WHERE id=? AND revision=? AND permission_generation=?",scope.Scope,scope.Revision,scope.Permission).ExpectAffectedRows(1),
                new SqliteCommand("UPDATE scan_runs SET phase=3,error='Superseded by a new complete reconciliation' WHERE scope_id=? AND phase IN(0,1)",scope.Scope),
                new SqliteCommand("INSERT INTO scan_runs(id,scope_id,permission_generation,scope_revision,log_start,phase,platform_upper_bound) VALUES(?,?,?,?,?,0,?)",run,scope.Scope,scope.Permission,scope.Revision,start,platformBoundary).ExpectAffectedRows(1)
            },token).ConfigureAwait(false); await PruneScans(token).ConfigureAwait(false);return run;
        }
        internal async Task Upsert(LibraryScopeState scope,string run,IReadOnlyList<ImageReference> images,CancellationToken token)
        {
            if(images.Count>32) throw new ArgumentException("Library commit pages contain at most 32 images.");
            var commands=new List<SqliteCommand>{new SqliteCommand("UPDATE library_scopes SET id=id WHERE id=? AND revision=? AND permission_generation=?",scope.Scope,scope.Revision,scope.Permission).ExpectAffectedRows(1)};
            foreach(var image in images)
            {
                string source=image.Source+":"+image.OriginId;
                commands.Add(new SqliteCommand(
                    "INSERT INTO change_log(scope_id,source_id,content_version,kind,scope_revision,permission_generation) " +
                    "SELECT ?,?,?,CASE WHEN s.source_id IS NULL OR s.present=0 THEN 0 WHEN s.content_version IS NOT ? THEN 1 ELSE 2 END,?,? " +
                    "FROM (SELECT 1) LEFT JOIN assets a ON a.source_id=? LEFT JOIN scope_assets s ON s.scope_id=? AND s.source_id=a.source_id " +
                    "WHERE s.source_id IS NULL OR s.present=0 OR s.asset_revision<>a.asset_revision OR a.content_version IS NOT ? OR a.name<>? OR a.mime<>? OR a.width<>? OR a.height<>?",
                    scope.Scope,source,image.Version,image.Version,scope.Revision,scope.Permission,source,scope.Scope,image.Version,image.FileName,image.MimeType,image.Width??0,image.Height??0));
                commands.Add(new SqliteCommand(
                    "INSERT INTO assets(source_id,content_version,name,mime,width,height) VALUES(?,?,?,?,?,?) " +
                    "ON CONFLICT(source_id) DO UPDATE SET asset_revision=assets.asset_revision+CASE WHEN assets.content_version IS NOT excluded.content_version OR assets.name<>excluded.name OR assets.mime<>excluded.mime OR assets.width<>excluded.width OR assets.height<>excluded.height THEN 1 ELSE 0 END,content_version=excluded.content_version,name=excluded.name,mime=excluded.mime,width=excluded.width,height=excluded.height",
                    source,image.Version,image.FileName,image.MimeType,image.Width??0,image.Height??0));
                commands.Add(new SqliteCommand(
                    "INSERT INTO scope_assets(scope_id,source_id,seen_scan_id,updated_seq,present,content_version,asset_revision) VALUES(?,?,?,(SELECT coalesce(max(sequence),0) FROM change_log),1,?,(SELECT asset_revision FROM assets WHERE source_id=?)) " +
                    "ON CONFLICT(scope_id,source_id) DO UPDATE SET seen_scan_id=excluded.seen_scan_id,updated_seq=excluded.updated_seq,present=1,content_version=excluded.content_version,asset_revision=excluded.asset_revision",
                    scope.Scope,source,run,image.Version,source));
            }
            await Commit(commands,token).ConfigureAwait(false);
        }
        internal async Task Finish(LibraryScopeState scope,string run,long start,CancellationToken token)
        {
            await Commit(new[]{new SqliteCommand("UPDATE scan_runs SET phase=1 WHERE id=? AND phase=0",run).ExpectAffectedRows(1)},token).ConfigureAwait(false);
            string cursor="";
            for(;;)
            {
                var missing=await Query(new SqliteCommand("SELECT source_id FROM scope_assets WHERE scope_id=? AND source_id>? AND present=1 AND seen_scan_id IS NOT ? AND updated_seq<=? ORDER BY source_id LIMIT 32",scope.Scope,cursor,run,start),r=>r.GetString(0),token).ConfigureAwait(false);
                if(missing.Count==0) break;
                var commands=new List<SqliteCommand>{new SqliteCommand("UPDATE library_scopes SET id=id WHERE id=? AND revision=? AND permission_generation=?",scope.Scope,scope.Revision,scope.Permission).ExpectAffectedRows(1)};
                foreach(string source in missing) AppendRemoval(commands,scope,source,scope.Access==(long)LibraryAccess.Limited?4:3);
                cursor=missing[missing.Count-1]; commands.Add(new SqliteCommand("UPDATE scan_runs SET missing_cursor=? WHERE id=? AND phase=1",cursor,run).ExpectAffectedRows(1));
                await Commit(commands,token).ConfigureAwait(false);
            }
            // Compare completed visible membership, never observer lifetime or an
            // ambiguous platform notification. Paging bounds memory for large scopes.
            string fingerprint=scope.Access==(long)LibraryAccess.Limited?await AccessFingerprint(scope.Scope,token).ConfigureAwait(false):null;
            await Commit(new[]{
                new SqliteCommand("UPDATE library_scopes SET completed_scan_id=?,platform_cursor=(SELECT platform_upper_bound FROM scan_runs WHERE id=?),requires_reconcile=0 WHERE id=? AND revision=? AND permission_generation=?",run,run,scope.Scope,scope.Revision,scope.Permission).ExpectAffectedRows(1),
                new SqliteCommand("UPDATE library_scopes SET revision=revision+1,permission_generation=permission_generation+1 WHERE id=? AND ? IS NOT NULL AND access_fingerprint IS NOT NULL AND access_fingerprint<>?",scope.Scope,fingerprint,fingerprint),
                new SqliteCommand("UPDATE library_scopes SET access_fingerprint=? WHERE id=?",fingerprint,scope.Scope).ExpectAffectedRows(1),
                new SqliteCommand("UPDATE scan_runs SET phase=2 WHERE id=? AND phase=1",run).ExpectAffectedRows(1)
            },token).ConfigureAwait(false);
            await PruneScans(token).ConfigureAwait(false);
        }
        async Task<string> AccessFingerprint(string scope,CancellationToken token)
        {
            using var sha=SHA256.Create();string after="";
            for(;;)
            {
                var page=await Query(new SqliteCommand("SELECT source_id FROM scope_assets WHERE scope_id=? AND present=1 AND source_id>? ORDER BY source_id LIMIT 200",scope,after),r=>r.GetString(0),token).ConfigureAwait(false);
                foreach(string source in page)
                {
                    // Length-prefix identities so adjacent strings cannot collide.
                    byte[] bytes=Encoding.UTF8.GetBytes(source);int length=bytes.Length;
                    byte[] prefix={(byte)length,(byte)(length>>8),(byte)(length>>16),(byte)(length>>24)};
                    sha.TransformBlock(prefix,0,prefix.Length,null,0);sha.TransformBlock(bytes,0,bytes.Length,null,0);after=source;
                }
                if(page.Count<200)break;
            }
            token.ThrowIfCancellationRequested();sha.TransformFinalBlock(Array.Empty<byte>(),0,0);
            return BitConverter.ToString(sha.Hash).Replace("-","").ToLowerInvariant();
        }
        static void AppendRemoval(List<SqliteCommand> commands,LibraryScopeState scope,string source,int kind)
        {
            commands.Add(new SqliteCommand("INSERT INTO change_log(scope_id,source_id,content_version,kind,scope_revision,permission_generation) SELECT ?,a.source_id,a.content_version,?,?,? FROM assets a JOIN scope_assets s ON s.source_id=a.source_id WHERE s.scope_id=? AND a.source_id=? AND s.present=1",scope.Scope,kind,scope.Revision,scope.Permission,scope.Scope,source));
            commands.Add(new SqliteCommand("UPDATE scope_assets SET present=0,updated_seq=(SELECT coalesce(max(sequence),0) FROM change_log) WHERE scope_id=? AND source_id=?",scope.Scope,source));
        }
        internal Task Remove(LibraryScopeState scope,string source,int kind,CancellationToken token)
        {
            var commands=new List<SqliteCommand>{new SqliteCommand("UPDATE library_scopes SET id=id WHERE id=? AND revision=? AND permission_generation=?",scope.Scope,scope.Revision,scope.Permission).ExpectAffectedRows(1)};
            AppendRemoval(commands,scope,source,kind); return Commit(commands,token);
        }
        internal Task<IReadOnlyList<ImageReference>> Page(string scope,string after,int size,CancellationToken token)
            => Query(new SqliteCommand("SELECT a.source_id,a.content_version,a.name,a.mime,a.width,a.height FROM scope_assets s JOIN assets a ON a.source_id=s.source_id WHERE s.scope_id=? AND s.present=1 AND s.source_id>? ORDER BY s.source_id LIMIT ?",scope,after,size),MapImage,token);
        static ImageReference MapImage(SqliteRow row)
        {
            string identity=row.GetString(0); int colon=identity.IndexOf(':'); if(colon<=0) throw new InvalidDataException("Invalid indexed image identity.");
            return new ImageReference(identity.Substring(0,colon),identity.Substring(colon+1),row.GetString(2),row.GetString(3),width:(int)row.GetInt64(4),height:(int)row.GetInt64(5),version:row.GetString(1));
        }
        // One SELECT owns one SQLite read snapshot, including an empty page.
        internal async Task<ImageLibraryChangeBatch> Changes(string scope,long after,int size,CancellationToken token)
        {
            var rows=await Query(new SqliteCommand(
                "WITH page AS (SELECT c.sequence,c.source_id,c.content_version,c.kind,a.name,a.mime FROM change_log c LEFT JOIN assets a ON a.source_id=c.source_id WHERE c.scope_id=? AND c.sequence>? ORDER BY c.sequence LIMIT ?) " +
                "SELECT l.library_id,l.index_generation,s.id,s.revision,s.permission_generation,l.retained_after_seq,s.requires_reconcile,max((SELECT coalesce(max(sequence),0) FROM change_log),l.retained_after_seq),p.sequence,p.source_id,p.content_version,p.kind,p.name,p.mime " +
                "FROM library_settings l JOIN library_scopes s ON s.id=? LEFT JOIN page p ON 1=1 WHERE l.singleton=1 ORDER BY p.sequence",scope,after,size,scope),
                r=>(position:new ImageLibraryPosition {LibraryId=r.GetString(0),IndexGeneration=r.GetInt64(1),ScopeId=r.GetString(2),ScopeRevision=r.GetInt64(3),PermissionGeneration=r.GetInt64(4),RetainedAfter=r.GetInt64(5),RequiresRefresh=r.GetInt64(6)!=0,Sequence=r.GetInt64(7)},
                    change:r.IsNull(8)?null:new ImageLibraryChange {Sequence=r.GetInt64(8),SourceIdentity=r.GetString(9),Version=r.GetString(10),Kind=(ImageLibraryChangeKind)r.GetInt64(11),Name=r.GetString(12),Mime=r.GetString(13)}),token).ConfigureAwait(false);
            if(rows.Count==0)throw new ArgumentException("Library scope not registered.");
            var position=rows[0].position;var changes=rows.Where(r=>r.change!=null).Select(r=>r.change).ToArray();
            if(changes.Length==size)position.Sequence=changes[changes.Length-1].Sequence;
            return new ImageLibraryChangeBatch {Position=position,Items=changes};
        }
        internal Task PruneScans(CancellationToken token) => Commit(new[]{new SqliteCommand(
            "DELETE FROM scan_runs WHERE id IN(SELECT r.id FROM scan_runs r WHERE r.phase IN(2,3) AND NOT EXISTS(SELECT 1 FROM library_scopes s WHERE s.completed_scan_id=r.id) ORDER BY r.phase,r.id LIMIT 200)")},token);
        internal Task<IReadOnlyList<(string source,string version)>> FileVersions(IReadOnlyList<ImageReference> images,CancellationToken token)
            => Query(new SqliteCommand("SELECT source_id,content_version FROM assets WHERE source_id IN("+string.Join(",",images.Select(_=>"?"))+")",images.Select(i=>(object)("file:"+i.OriginId)).ToArray()),r=>(r.GetString(0),r.GetString(1)),token);
        internal async Task PruneChanges(long through,CancellationToken token)
        {
            await Commit(new[]{
                new SqliteCommand("UPDATE library_settings SET retained_after_seq=max(retained_after_seq,coalesce((SELECT max(sequence) FROM (SELECT sequence FROM change_log WHERE sequence<=? ORDER BY sequence LIMIT 200)),retained_after_seq)) WHERE singleton=1",through),
                new SqliteCommand("DELETE FROM change_log WHERE sequence IN(SELECT sequence FROM change_log WHERE sequence<=(SELECT retained_after_seq FROM library_settings WHERE singleton=1) ORDER BY sequence LIMIT 200)")
            },token).ConfigureAwait(false);
        }
        internal Task CloseAsync() => database.CloseAsync();
    }
    internal sealed class LibraryScopeState
    {
        internal string Scope;
        internal long Revision,Permission,Access;
        internal bool RequiresReconcile;
    }
}
