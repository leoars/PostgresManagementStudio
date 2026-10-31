using Npgsql;
using PGMS.PostgreSql;

internal static class RevokePreviewChecks
{
    internal static async Task RunAsync(string connection,Action<bool,string> check)
    {
        await using var f=await FutureDefaultFixture.CreateAsync(connection);
        string suffix=Guid.NewGuid().ToString("N"),child="preview_child_"+suffix,grand="preview_grand_"+suffix;
        string qc=SqlIdentifier.Quote(child),qg=SqlIdentifier.Quote(grand);
        await f.Admin($"CREATE ROLE {qc} LOGIN;CREATE ROLE {qg};GRANT USAGE ON SCHEMA {f.QS} TO {f.QR},{qc}");
        await using var childConnection=new NpgsqlConnection(new NpgsqlConnectionStringBuilder(connection){Username=child,Pooling=false}.ConnectionString);await childConnection.OpenAsync();
        async Task Child(string sql){await using var c=new NpgsqlCommand(sql,childConnection);await c.ExecuteNonQueryAsync();}
        try
        {
            await f.Owner($"CREATE TABLE {f.QS}.target(value integer,other integer);GRANT SELECT ON {f.QS}.target TO {f.QR} WITH GRANT OPTION;GRANT UPDATE ON {f.QS}.target TO {f.QR} WITH GRANT OPTION");
            await f.Reader($"GRANT SELECT ON {f.QS}.target TO {qc} WITH GRANT OPTION");await Child($"GRANT SELECT ON {f.QS}.target TO {qg}");
            var request=new PrivilegeRequest(PrivilegeTarget.Table,"target",f.Recipient,"SELECT",Revoke:true,Schema:f.Schema);
            var candidates=await new PrivilegeGrantorCatalog(f.CreatorConnection).InspectAsync(request);var owner=candidates.Candidates.Single(x=>x.Name==f.Creator);
            request=request with{Grantor=new(candidates.Target.DatabaseOid,owner.Oid,owner.Name)};
            var inspector=new PrivilegeRevokeInspector(f.CreatorConnection);var preview=await inspector.InspectAsync(request);
            check(preview.Target==candidates.Target && preview.Recipient.Name==f.Recipient && preview.Request==request,"Revoke preview binds request, target, recipient and grantor identities");
            check(preview.PotentialChanges.Count==3 && preview.PotentialChanges.Count(x=>x.Direct)==1,"Preview includes direct grant and two levels of dependent delegation");
            check(preview.PotentialChanges.All(x=>x.Privilege=="SELECT") && preview.PotentialChanges.Any(x=>x.Recipient==grand),"Preview excludes unrelated UPDATE while retaining terminal recipient");
            var option=await inspector.InspectAsync(request with{GrantOption=true});
            check(option.PotentialChanges.Count==3 && option.Request.GrantOption,"Grant-option-only preview retains dependency chain and explicit intent");
            await f.Owner($"GRANT SELECT ON {f.QS}.target TO {qc} WITH GRANT OPTION");
            var independent=await inspector.InspectAsync(request);
            check(independent.PotentialChanges.Any(x=>x.Recipient==grand) && independent.PotentialChanges.All(x=>!(x.Grantor==f.Creator && x.Recipient==child)),"Conservative preview includes possible descendants without claiming independent owner grant is removed");
            await f.Owner($"REVOKE SELECT ON {f.QS}.target FROM {f.QR} CASCADE");
            var actual=await new EffectivePrivilegeService(connection).InspectAsync(request.Target,request.Name,f.Schema);
            check(actual.AclEntries.Any(x=>x.Grantee==grand && x.Privilege=="SELECT"),"Native CASCADE preserves descendant through independent grant option path");
            check((await inspector.InspectAsync(request)).PotentialChanges.Count==0,"Absent selected root produces empty preview without inventing removals");
            await f.Owner($"GRANT SELECT(value) ON {f.QS}.target TO {f.QR} WITH GRANT OPTION");await f.Reader($"GRANT SELECT(value) ON {f.QS}.target TO {qc}");
            var column=await inspector.InspectAsync(request with{Column="value"});
            check(column.Target.ColumnNumber==1 && column.PotentialChanges.Count==2 && column.PotentialChanges.All(x=>x.ColumnNumber==1 && x.Column=="value"),"Column preview captures only selected attnum and its delegation");
            check((await inspector.InspectAsync(request with{Column="other"})).PotentialChanges.Count==0,"Another column has no fabricated dependency");
            var table=await inspector.InspectAsync(request);
            check(table.PotentialChanges.Any(x=>x.ColumnNumber==1),"Whole-table revoke preview conservatively includes same-privilege column grants");
            await f.Owner($"GRANT SELECT ON {f.QS}.target TO PUBLIC");
            var pub=await inspector.InspectAsync(request with{RecipientIsPublic=true,Role=""});
            check(pub.PotentialChanges.Count==1 && pub.PotentialChanges[0].RecipientOid==0 && pub.PotentialChanges[0].Recipient=="PUBLIC","PUBLIC is a terminal ACL recipient in revoke preview");
            await f.Owner($"CREATE TABLE {f.QS}.empty_acl(value integer);REVOKE ALL ON {f.QS}.empty_acl FROM {f.QO}");
            check((await inspector.InspectAsync(request with{Name="empty_acl"})).PotentialChanges.Count==0,"Explicit empty ACL is inspected without aclexplode array errors");
            check(await new PrivilegeService(f.CreatorConnection).ApplyCurrentAsync(new(PrivilegeTarget.Table,"empty_acl",f.Recipient,"SELECT",Schema:f.Schema)),"Shared mutation guard also handles an explicit empty ACL");
            var service = new PrivilegeService(f.CreatorConnection);
            foreach (var apply in new Func<Task>[] {
                () => service.ApplyAsync(request with { Cascade = true }),
                async () => { await service.ApplyCurrentAsync(request with { Cascade = true }); },
                async () => { await service.ApplyInspectedAsync(request with { Cascade = true }, preview.Target, preview.Recipient); } })
            {
                bool refused = false; try { await apply(); } catch (ArgumentException) { refused = true; }
                check(refused, "Ordinary Apply APIs refuse CASCADE without reviewed dependencies");
            }
            check((request with { Cascade = true }).ToSql().Contains(" CASCADE;"), "Explicit CASCADE SQL preserves SET LOCAL ROLE wrapper");
            var stale = await inspector.InspectAsync(request with { Column = "value" });
            await f.Owner($"GRANT INSERT ON {f.QS}.target TO {f.QR}");
            bool staleRefused = false; try { await service.ApplyReviewedRevokeAsync(stale); } catch (InvalidOperationException) { staleRefused = true; }
            check(staleRefused, "Reviewed revoke rejects changed complete ACL, including unrelated privileges");
            var renamed = await inspector.InspectAsync(request with { Column = "value" });
            await f.Admin($"ALTER ROLE {qc} RENAME TO {SqlIdentifier.Quote(child + "_renamed")}");
            bool renameRefused = false; try { await service.ApplyReviewedRevokeAsync(renamed); } catch (InvalidOperationException) { renameRefused = true; }
            await f.Admin($"ALTER ROLE {SqlIdentifier.Quote(child + "_renamed")} RENAME TO {qc}");
            check(renameRefused, "Reviewed revoke rejects renamed dependent recipient even when ACL OIDs are unchanged");
            var columnRename = await inspector.InspectAsync(request);
            await f.Owner($"ALTER TABLE {f.QS}.target RENAME COLUMN value TO renamed_value");
            bool columnRefused = false; try { await service.ApplyReviewedRevokeAsync(columnRename); } catch (InvalidOperationException) { columnRefused = true; }
            await f.Owner($"ALTER TABLE {f.QS}.target RENAME COLUMN renamed_value TO value");
            check(columnRefused, "Whole-table reviewed revoke rejects renamed affected column");
            await f.Owner($"CREATE TABLE {f.QS}.cascade_target(value integer);GRANT SELECT ON {f.QS}.cascade_target TO {f.QR} WITH GRANT OPTION;GRANT UPDATE ON {f.QS}.cascade_target TO {f.QR}");
            await f.Reader($"GRANT SELECT ON {f.QS}.cascade_target TO {qc} WITH GRANT OPTION");
            await Child($"GRANT SELECT ON {f.QS}.cascade_target TO {qg}");
            var cascadeRequest = request with { Name = "cascade_target", GrantOption = true };
            var cascadePreview = await inspector.InspectAsync(cascadeRequest);
            check(await service.ApplyReviewedRevokeAsync(cascadePreview), "Reviewed CASCADE grant-option revoke commits through common owner");
            var cascadeAcl = await new EffectivePrivilegeService(connection).InspectAsync(PrivilegeTarget.Table, "cascade_target", f.Schema);
            check(cascadeAcl.AclEntries.Any(x => x.Grantee == f.Recipient && x.Privilege == "SELECT" && !x.GrantOption), "CASCADE GRANT OPTION FOR preserves root privilege");
            check(!cascadeAcl.AclEntries.Any(x => (x.Grantee == child || x.Grantee == grand) && x.Privilege == "SELECT"), "CASCADE removes two levels of dependent grants");
            check(cascadeAcl.AclEntries.Any(x => x.Grantee == f.Recipient && x.Privilege == "UPDATE"), "CASCADE preserves unrelated UPDATE");
            await f.Owner($"GRANT SELECT ON {f.QS}.cascade_target TO {f.QR} WITH GRANT OPTION;GRANT SELECT ON {f.QS}.cascade_target TO {qc} WITH GRANT OPTION");
            await f.Reader($"GRANT SELECT ON {f.QS}.cascade_target TO {qc} WITH GRANT OPTION");await Child($"GRANT SELECT ON {f.QS}.cascade_target TO {qg}");
            var independentPreview = await inspector.InspectAsync(cascadeRequest with { GrantOption = false });
            check(await service.ApplyReviewedRevokeAsync(independentPreview), "Reviewed conservative CASCADE accepts server-preserved independent path");
            cascadeAcl = await new EffectivePrivilegeService(connection).InspectAsync(PrivilegeTarget.Table, "cascade_target", f.Schema);
            check(cascadeAcl.AclEntries.Any(x => x.Grantee == grand && x.Privilege == "SELECT"), "Independent grant path retains terminal recipient after guarded CASCADE");
            check(!await service.ApplyReviewedRevokeAsync(await inspector.InspectAsync(cascadeRequest with { GrantOption = false })), "Reviewed empty revoke returns rolled-back no-op");
            var readOnly=new NpgsqlConnectionStringBuilder(f.CreatorConnection){Options="-c default_transaction_read_only=on"}.ConnectionString;
            var observed=await new PrivilegeRevokeInspector(readOnly).InspectAsync(request with{Column="value"});
            check(observed.PotentialChanges.Count==column.PotentialChanges.Count,"Preview succeeds under server-enforced read-only defaults");
            var after=await new EffectivePrivilegeService(connection).InspectAsync(request.Target,request.Name,f.Schema);
            check(after.AclEntries.Any(x=>x.Grantee==child && x.ColumnNumber==1 && x.Privilege=="SELECT"),"Preview leaves actual delegated column ACL untouched");
            bool immutable=false;try{((IList<PrivilegeRevokeImpact>)preview.PotentialChanges).Clear();}catch(NotSupportedException){immutable=true;}
            check(immutable,"Reviewed impact list cannot be edited through collection interface");
            foreach(var invalid in new[]{request with{Revoke=false},request with{Grantor=null}})
            {bool refused=false;try{await new PrivilegeRevokeInspector("invalid").InspectAsync(invalid);}catch(ArgumentException){refused=true;}check(refused,"Preview rejects missing revoke/grantor intent before opening connection");}
            using var cancel=new CancellationTokenSource();cancel.Cancel();bool cancelled=false;
            try{await new PrivilegeRevokeInspector("invalid").InspectAsync(request,cancel.Token);}catch(OperationCanceledException){cancelled=true;}
            check(cancelled,"Pre-cancelled revoke preview does not open a connection");
        }
        finally{await childConnection.CloseAsync();await f.Admin($"DROP SCHEMA IF EXISTS {f.QS} CASCADE;DROP OWNED BY {qc},{qg};DROP ROLE {qc},{qg}");}
    }
}
