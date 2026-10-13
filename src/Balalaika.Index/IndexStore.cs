using System.Text;
using Microsoft.Data.Sqlite;

namespace Balalaika.Index;

/// <summary>
/// SQLite-backed derived cache of symbols, references and relations.
///
/// Storage is content-addressed: a <c>blob</c> row holds everything extracted from one file *content* within one
/// project. The <c>tree</c> table maps the current working-tree path of each file to a blob. Switching git branches
/// therefore only rewrites <c>tree</c>; blobs for content seen before are reused as-is. Every query joins through
/// <c>tree</c>, so only blobs that describe the current working tree are ever returned.
///
/// A single connection guarded by a lock is used. Throughput is not a concern at this scale; correctness is.
/// </summary>
public sealed class IndexStore : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly object _gate = new();

    public string DatabasePath { get; }

    public IndexStore(string databasePath)
    {
        this.DatabasePath = databasePath;
        string? dir = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        this._connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
        }.ToString());
        this._connection.Open();
        this.Execute("PRAGMA journal_mode=WAL;");
        this.Execute("PRAGMA synchronous=NORMAL;");
        this.Execute("PRAGMA foreign_keys=ON;");
        this.CreateSchema();
    }

    private void CreateSchema()
    {
        this.Execute("""
            CREATE TABLE IF NOT EXISTS project(
                id INTEGER PRIMARY KEY,
                path TEXT NOT NULL UNIQUE COLLATE NOCASE,
                name TEXT NOT NULL,
                assembly_name TEXT NOT NULL,
                solution_path TEXT NOT NULL COLLATE NOCASE);

            CREATE TABLE IF NOT EXISTS blob(
                id INTEGER PRIMARY KEY,
                project_id INTEGER NOT NULL REFERENCES project(id) ON DELETE CASCADE,
                content_hash TEXT NOT NULL,
                created_at INTEGER NOT NULL,
                last_used_at INTEGER NOT NULL,
                UNIQUE(project_id, content_hash));

            CREATE TABLE IF NOT EXISTS symbol(
                id INTEGER PRIMARY KEY,
                blob_id INTEGER NOT NULL REFERENCES blob(id) ON DELETE CASCADE,
                doc_id TEXT NOT NULL,
                kind TEXT NOT NULL,
                name TEXT NOT NULL,
                fq_name TEXT NOT NULL,
                signature TEXT NOT NULL,
                accessibility TEXT NOT NULL,
                containing_doc_id TEXT,
                line INTEGER NOT NULL,
                col INTEGER NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_symbol_doc ON symbol(doc_id);
            CREATE INDEX IF NOT EXISTS ix_symbol_blob ON symbol(blob_id);
            CREATE INDEX IF NOT EXISTS ix_symbol_name ON symbol(name COLLATE NOCASE);

            CREATE TABLE IF NOT EXISTS reference(
                id INTEGER PRIMARY KEY,
                blob_id INTEGER NOT NULL REFERENCES blob(id) ON DELETE CASCADE,
                target_doc_id TEXT NOT NULL,
                target_assembly TEXT NOT NULL,
                kind TEXT NOT NULL,
                line INTEGER NOT NULL,
                col INTEGER NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_reference_target ON reference(target_doc_id);
            CREATE INDEX IF NOT EXISTS ix_reference_blob ON reference(blob_id);

            CREATE TABLE IF NOT EXISTS relation(
                id INTEGER PRIMARY KEY,
                blob_id INTEGER NOT NULL REFERENCES blob(id) ON DELETE CASCADE,
                from_doc_id TEXT NOT NULL,
                to_doc_id TEXT NOT NULL,
                to_assembly TEXT NOT NULL,
                kind TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_relation_to ON relation(to_doc_id);
            CREATE INDEX IF NOT EXISTS ix_relation_blob ON relation(blob_id);

            CREATE TABLE IF NOT EXISTS tree(
                project_id INTEGER NOT NULL REFERENCES project(id) ON DELETE CASCADE,
                path TEXT NOT NULL COLLATE NOCASE,
                content_hash TEXT NOT NULL,
                mtime INTEGER NOT NULL,
                size INTEGER NOT NULL,
                seen_at INTEGER NOT NULL,
                PRIMARY KEY(project_id, path));
            CREATE INDEX IF NOT EXISTS ix_tree_hash ON tree(project_id, content_hash);

            CREATE VIRTUAL TABLE IF NOT EXISTS symbol_fts USING fts5(
                name, fq_name, content='symbol', content_rowid='id', tokenize='unicode61');

            CREATE TRIGGER IF NOT EXISTS symbol_ai AFTER INSERT ON symbol BEGIN
                INSERT INTO symbol_fts(rowid, name, fq_name) VALUES (new.id, new.name, new.fq_name);
            END;
            CREATE TRIGGER IF NOT EXISTS symbol_ad AFTER DELETE ON symbol BEGIN
                INSERT INTO symbol_fts(symbol_fts, rowid, name, fq_name) VALUES ('delete', old.id, old.name, old.fq_name);
            END;

            CREATE TABLE IF NOT EXISTS package_ref(
                project_id INTEGER NOT NULL REFERENCES project(id) ON DELETE CASCADE,
                assembly_name TEXT NOT NULL COLLATE NOCASE,
                package_id TEXT NOT NULL,
                version TEXT NOT NULL,
                PRIMARY KEY(project_id, assembly_name));
            CREATE INDEX IF NOT EXISTS ix_package_ref_asm ON package_ref(assembly_name);

            CREATE TABLE IF NOT EXISTS excluded_file(
                project_id INTEGER NOT NULL REFERENCES project(id) ON DELETE CASCADE,
                path TEXT NOT NULL COLLATE NOCASE,
                PRIMARY KEY(project_id, path));
            """);

        // Columns added after the first release; CREATE TABLE IF NOT EXISTS does not add them to existing databases.
        this.EnsureColumn("project", "version", "TEXT");
        this.EnsureColumn("project", "indexed_schema", "INTEGER NOT NULL DEFAULT 0");
        this.EnsureColumn("symbol", "end_line", "INTEGER NOT NULL DEFAULT 0");
        this.EnsureColumn("reference", "from_doc_id", "TEXT");
    }

    /// <summary>
    /// Bumped when an index pass starts writing data that older passes did not. Each project records the version it
    /// was last indexed under, so a freshness check can tell "unchanged" from "indexed before this data existed", and
    /// <c>SolutionIndexer</c> re-extracts every document of a project stamped with an older version.
    /// 2: project version and package references.
    /// 3: files under a project directory that its compilation excludes.
    /// 4: enclosing declaration per reference and end line per symbol.
    /// </summary>
    public const int SchemaVersion = 4;

    private void EnsureColumn(string table, string column, string type)
    {
        using SqliteCommand cmd = this._connection.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table})";
        using SqliteDataReader r = cmd.ExecuteReader();
        while (r.Read())
        {
            if (r.GetString(1).Equals(column, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        r.Close();
        this.Execute($"ALTER TABLE {table} ADD COLUMN {column} {type}");
    }

    // ---------------------------------------------------------------- projects

    public int EnsureProject(string path, string name, string assemblyName, string solutionPath)
    {
        lock (this._gate)
        {
            using SqliteCommand cmd = this._connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO project(path, name, assembly_name, solution_path) VALUES ($path, $name, $asm, $sln)
                ON CONFLICT(path) DO UPDATE SET name = excluded.name, assembly_name = excluded.assembly_name, solution_path = excluded.solution_path
                RETURNING id;
                """;
            cmd.Parameters.AddWithValue("$path", path);
            cmd.Parameters.AddWithValue("$name", name);
            cmd.Parameters.AddWithValue("$asm", assemblyName);
            cmd.Parameters.AddWithValue("$sln", solutionPath);
            return Convert.ToInt32(cmd.ExecuteScalar());
        }
    }

    public IReadOnlyList<ProjectRecord> GetProjects()
    {
        lock (this._gate)
        {
            using SqliteCommand cmd = this._connection.CreateCommand();
            cmd.CommandText = $"SELECT {ProjectColumns("project")} FROM project ORDER BY path";
            using SqliteDataReader r = cmd.ExecuteReader();
            List<ProjectRecord> list = [];
            while (r.Read())
            {
                list.Add(ReadProject(r, 0));
            }

            return list;
        }
    }

    /// <summary>The seven project columns in the order <see cref="ReadProject"/> expects.</summary>
    private static string ProjectColumns(string alias)
    {
        return $"{alias}.id, {alias}.path, {alias}.name, {alias}.assembly_name, {alias}.solution_path, {alias}.version, {alias}.indexed_schema";
    }

    private const int ProjectColumnCount = 7;

    /// <summary>The <see cref="SchemaVersion"/> a project was last indexed under; 0 for a project never stamped.</summary>
    public int GetIndexedSchema(int projectId)
    {
        lock (this._gate)
        {
            return (int)(this.ScalarLong("SELECT indexed_schema FROM project WHERE id = $p", ("$p", projectId)) ?? 0);
        }
    }

    private static ProjectRecord ReadProject(SqliteDataReader r, int offset)
    {
        return new ProjectRecord(
            r.GetInt32(offset), r.GetString(offset + 1), r.GetString(offset + 2), r.GetString(offset + 3), r.GetString(offset + 4),
            r.IsDBNull(offset + 5) ? null : r.GetString(offset + 5),
            r.GetInt32(offset + 6));
    }

    // ---------------------------------------------------------------- packages

    /// <summary>Records a project's own package version (null when it declares none) and the packages it compiles against. Replaces the previous set.</summary>
    public void SetProjectPackages(int projectId, string? version, IReadOnlyList<PackageRefRecord> packages)
    {
        lock (this._gate)
        {
            using SqliteTransaction tx = this._connection.BeginTransaction();
            this.Execute("UPDATE project SET version = $v, indexed_schema = $s WHERE id = $p", ("$p", projectId), ("$v", (object?)version ?? DBNull.Value), ("$s", SchemaVersion));
            this.Execute("DELETE FROM package_ref WHERE project_id = $p", ("$p", projectId));
            foreach (PackageRefRecord p in packages)
            {
                this.Execute(
                    "INSERT OR REPLACE INTO package_ref(project_id, assembly_name, package_id, version) VALUES ($p, $asm, $id, $v)",
                    ("$p", projectId), ("$asm", p.AssemblyName), ("$id", p.PackageId), ("$v", p.Version));
            }

            tx.Commit();
        }
    }

    /// <summary>Every indexed project that compiles against <paramref name="assemblyName"/> through a NuGet package, with the version it uses.</summary>
    public IReadOnlyList<(ProjectRecord Consumer, PackageRefRecord Package)> GetPackageConsumers(string assemblyName)
    {
        lock (this._gate)
        {
            using SqliteCommand cmd = this._connection.CreateCommand();
            cmd.CommandText = $"""
                SELECT {ProjectColumns("c")}, pr.assembly_name, pr.package_id, pr.version
                FROM package_ref pr
                JOIN project c ON c.id = pr.project_id
                WHERE pr.assembly_name = $asm
                ORDER BY c.solution_path, c.name
                """;
            cmd.Parameters.AddWithValue("$asm", assemblyName);
            using SqliteDataReader r = cmd.ExecuteReader();
            List<(ProjectRecord, PackageRefRecord)> list = [];
            while (r.Read())
            {
                int n = ProjectColumnCount;
                list.Add((ReadProject(r, 0), new PackageRefRecord(r.GetString(n), r.GetString(n + 1), r.GetString(n + 2))));
            }

            return list;
        }
    }

    /// <summary>
    /// Consumers whose package version differs from the version an indexed source project with the same assembly name
    /// currently declares. Sources without a declared version are not compared.
    /// </summary>
    public IReadOnlyList<PackageDriftRow> FindPackageDrift()
    {
        lock (this._gate)
        {
            using SqliteCommand cmd = this._connection.CreateCommand();
            cmd.CommandText = $"""
                SELECT {ProjectColumns("c")}, pr.package_id, pr.version, {ProjectColumns("s")}
                FROM package_ref pr
                JOIN project c ON c.id = pr.project_id
                JOIN project s ON s.assembly_name = pr.assembly_name COLLATE NOCASE AND s.version IS NOT NULL AND s.id <> c.id
                WHERE pr.version <> s.version COLLATE NOCASE
                ORDER BY s.assembly_name, s.solution_path, c.solution_path, c.name
                """;
            using SqliteDataReader r = cmd.ExecuteReader();
            List<PackageDriftRow> list = [];
            while (r.Read())
            {
                int n = ProjectColumnCount;
                list.Add(new PackageDriftRow(ReadProject(r, 0), r.GetString(n), r.GetString(n + 1), ReadProject(r, n + 2)));
            }

            return list;
        }
    }

    // ---------------------------------------------------------------- blobs

    public bool HasBlob(int projectId, string contentHash)
    {
        lock (this._gate)
        {
            using SqliteCommand cmd = this._connection.CreateCommand();
            cmd.CommandText = "SELECT 1 FROM blob WHERE project_id = $p AND content_hash = $h";
            cmd.Parameters.AddWithValue("$p", projectId);
            cmd.Parameters.AddWithValue("$h", contentHash);
            return cmd.ExecuteScalar() is not null;
        }
    }

    /// <summary>Writes a blob for the given content. Replaces an existing blob with the same hash when <paramref name="replace"/> is set.</summary>
    public void WriteBlob(int projectId, string contentHash, DocumentExtraction extraction, bool replace = false)
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        lock (this._gate)
        {
            using SqliteTransaction tx = this._connection.BeginTransaction();

            long? existing = this.ScalarLong("SELECT id FROM blob WHERE project_id = $p AND content_hash = $h", ("$p", projectId), ("$h", contentHash));
            if (existing is not null)
            {
                if (!replace)
                {
                    tx.Commit();
                    return;
                }

                this.DeleteBlobRows(existing.Value);
            }

            long blobId = existing ?? this.ScalarLong(
                "INSERT INTO blob(project_id, content_hash, created_at, last_used_at) VALUES ($p, $h, $now, $now) RETURNING id",
                ("$p", projectId), ("$h", contentHash), ("$now", now))!.Value;

            using (SqliteCommand cmd = this._connection.CreateCommand())
            {
                cmd.CommandText = """
                    INSERT INTO symbol(blob_id, doc_id, kind, name, fq_name, signature, accessibility, containing_doc_id, line, col, end_line)
                    VALUES ($b, $doc, $kind, $name, $fq, $sig, $acc, $cont, $line, $col, $end)
                    """;
                SqliteParameter pDoc = cmd.Parameters.Add("$doc", SqliteType.Text);
                SqliteParameter pKind = cmd.Parameters.Add("$kind", SqliteType.Text);
                SqliteParameter pName = cmd.Parameters.Add("$name", SqliteType.Text);
                SqliteParameter pFq = cmd.Parameters.Add("$fq", SqliteType.Text);
                SqliteParameter pSig = cmd.Parameters.Add("$sig", SqliteType.Text);
                SqliteParameter pAcc = cmd.Parameters.Add("$acc", SqliteType.Text);
                SqliteParameter pCont = cmd.Parameters.Add("$cont", SqliteType.Text);
                SqliteParameter pLine = cmd.Parameters.Add("$line", SqliteType.Integer);
                SqliteParameter pCol = cmd.Parameters.Add("$col", SqliteType.Integer);
                SqliteParameter pEnd = cmd.Parameters.Add("$end", SqliteType.Integer);
                cmd.Parameters.AddWithValue("$b", blobId);
                foreach (SymbolRecord s in extraction.Symbols)
                {
                    pDoc.Value = s.DocId;
                    pKind.Value = s.Kind;
                    pName.Value = s.Name;
                    pFq.Value = s.FqName;
                    pSig.Value = s.Signature;
                    pAcc.Value = s.Accessibility;
                    pCont.Value = (object?)s.ContainingDocId ?? DBNull.Value;
                    pLine.Value = s.Line;
                    pCol.Value = s.Column;
                    pEnd.Value = s.EndLine;
                    cmd.ExecuteNonQuery();
                }
            }

            using (SqliteCommand cmd = this._connection.CreateCommand())
            {
                cmd.CommandText = "INSERT INTO reference(blob_id, target_doc_id, target_assembly, kind, line, col, from_doc_id) VALUES ($b, $doc, $asm, $kind, $line, $col, $from)";
                SqliteParameter pDoc = cmd.Parameters.Add("$doc", SqliteType.Text);
                SqliteParameter pAsm = cmd.Parameters.Add("$asm", SqliteType.Text);
                SqliteParameter pKind = cmd.Parameters.Add("$kind", SqliteType.Text);
                SqliteParameter pLine = cmd.Parameters.Add("$line", SqliteType.Integer);
                SqliteParameter pCol = cmd.Parameters.Add("$col", SqliteType.Integer);
                SqliteParameter pFrom = cmd.Parameters.Add("$from", SqliteType.Text);
                cmd.Parameters.AddWithValue("$b", blobId);
                foreach (ReferenceRecord rr in extraction.References)
                {
                    pDoc.Value = rr.TargetDocId;
                    pAsm.Value = rr.TargetAssembly;
                    pKind.Value = rr.Kind;
                    pLine.Value = rr.Line;
                    pCol.Value = rr.Column;
                    pFrom.Value = (object?)rr.FromDocId ?? DBNull.Value;
                    cmd.ExecuteNonQuery();
                }
            }

            using (SqliteCommand cmd = this._connection.CreateCommand())
            {
                cmd.CommandText = "INSERT INTO relation(blob_id, from_doc_id, to_doc_id, to_assembly, kind) VALUES ($b, $from, $to, $asm, $kind)";
                SqliteParameter pFrom = cmd.Parameters.Add("$from", SqliteType.Text);
                SqliteParameter pTo = cmd.Parameters.Add("$to", SqliteType.Text);
                SqliteParameter pAsm = cmd.Parameters.Add("$asm", SqliteType.Text);
                SqliteParameter pKind = cmd.Parameters.Add("$kind", SqliteType.Text);
                cmd.Parameters.AddWithValue("$b", blobId);
                foreach (RelationRecord rel in extraction.Relations)
                {
                    pFrom.Value = rel.FromDocId;
                    pTo.Value = rel.ToDocId;
                    pAsm.Value = rel.ToAssembly;
                    pKind.Value = rel.Kind;
                    cmd.ExecuteNonQuery();
                }
            }

            tx.Commit();
        }
    }

    /// <summary>
    /// The <see cref="DeclarationSurface"/> hash of a stored blob, computed from its symbol and relation rows, or null
    /// when no such blob exists. Used to decide whether a content change can affect how other documents bind.
    /// </summary>
    public string? GetSurfaceHash(int projectId, string contentHash)
    {
        lock (this._gate)
        {
            long? blobId = this.ScalarLong("SELECT id FROM blob WHERE project_id = $p AND content_hash = $h", ("$p", projectId), ("$h", contentHash));
            if (blobId is null)
            {
                return null;
            }

            List<SymbolRecord> symbols = [];
            using (SqliteCommand cmd = this._connection.CreateCommand())
            {
                cmd.CommandText = "SELECT doc_id, signature, accessibility FROM symbol WHERE blob_id = $b";
                cmd.Parameters.AddWithValue("$b", blobId.Value);
                using SqliteDataReader r = cmd.ExecuteReader();
                while (r.Read())
                {
                    symbols.Add(new SymbolRecord(r.GetString(0), "", "", "", r.GetString(1), r.GetString(2), null, 0, 0));
                }
            }

            List<RelationRecord> relations = [];
            using (SqliteCommand cmd = this._connection.CreateCommand())
            {
                cmd.CommandText = "SELECT from_doc_id, to_doc_id, to_assembly, kind FROM relation WHERE blob_id = $b";
                cmd.Parameters.AddWithValue("$b", blobId.Value);
                using SqliteDataReader r = cmd.ExecuteReader();
                while (r.Read())
                {
                    relations.Add(new RelationRecord(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3)));
                }
            }

            return DeclarationSurface.Hash(symbols, relations);
        }
    }

    private void DeleteBlobRows(long blobId)
    {
        // Delete child rows explicitly so the FTS delete trigger fires for symbols.
        this.Execute("DELETE FROM symbol WHERE blob_id = $b", ("$b", blobId));
        this.Execute("DELETE FROM reference WHERE blob_id = $b", ("$b", blobId));
        this.Execute("DELETE FROM relation WHERE blob_id = $b", ("$b", blobId));
    }

    // ---------------------------------------------------------------- excluded files

    /// <summary>
    /// Records the <c>.cs</c> files under a project's directory that the loaded project did not compile (a
    /// <c>Compile Remove</c> glob, typically), so the freshness check does not mistake them for new files. Replaces the
    /// previous set.
    /// </summary>
    public void SetExcludedFiles(int projectId, IReadOnlyCollection<string> paths)
    {
        lock (this._gate)
        {
            using SqliteTransaction tx = this._connection.BeginTransaction();
            this.Execute("DELETE FROM excluded_file WHERE project_id = $p", ("$p", projectId));
            foreach (string path in paths)
            {
                this.Execute("INSERT OR REPLACE INTO excluded_file(project_id, path) VALUES ($p, $path)", ("$p", projectId), ("$path", path));
            }

            tx.Commit();
        }
    }

    public IReadOnlySet<string> GetExcludedFiles(int projectId)
    {
        lock (this._gate)
        {
            using SqliteCommand cmd = this._connection.CreateCommand();
            cmd.CommandText = "SELECT path FROM excluded_file WHERE project_id = $p";
            cmd.Parameters.AddWithValue("$p", projectId);
            using SqliteDataReader r = cmd.ExecuteReader();
            HashSet<string> set = new(StringComparer.OrdinalIgnoreCase);
            while (r.Read())
            {
                set.Add(r.GetString(0));
            }

            return set;
        }
    }

    // ---------------------------------------------------------------- tree

    public IReadOnlyDictionary<string, TreeEntry> GetTree(int projectId)
    {
        lock (this._gate)
        {
            using SqliteCommand cmd = this._connection.CreateCommand();
            cmd.CommandText = "SELECT path, content_hash, mtime, size FROM tree WHERE project_id = $p";
            cmd.Parameters.AddWithValue("$p", projectId);
            using SqliteDataReader r = cmd.ExecuteReader();
            Dictionary<string, TreeEntry> map = new(StringComparer.OrdinalIgnoreCase);
            while (r.Read())
            {
                string path = r.GetString(0);
                map[path] = new TreeEntry(path, r.GetString(1), r.GetInt64(2), r.GetInt64(3));
            }

            return map;
        }
    }

    /// <summary>The tree entry of a path within one project, or null when that project does not list the path.</summary>
    public TreeEntry? GetTreeEntry(int projectId, string path)
    {
        lock (this._gate)
        {
            using SqliteCommand cmd = this._connection.CreateCommand();
            cmd.CommandText = "SELECT content_hash, mtime, size FROM tree WHERE project_id = $p AND path = $path";
            cmd.Parameters.AddWithValue("$p", projectId);
            cmd.Parameters.AddWithValue("$path", path);
            using SqliteDataReader r = cmd.ExecuteReader();
            return r.Read() ? new TreeEntry(path, r.GetString(0), r.GetInt64(1), r.GetInt64(2)) : null;
        }
    }

    /// <summary>Every project's tree entry for a path. A file linked into several projects has one per project.</summary>
    public IReadOnlyList<(ProjectRecord Project, TreeEntry Entry)> FindTreeEntries(string path)
    {
        lock (this._gate)
        {
            using SqliteCommand cmd = this._connection.CreateCommand();
            cmd.CommandText = $"""
                SELECT {ProjectColumns("p")}, t.content_hash, t.mtime, t.size
                FROM tree t
                JOIN project p ON p.id = t.project_id
                WHERE t.path = $path
                """;
            cmd.Parameters.AddWithValue("$path", path);
            using SqliteDataReader r = cmd.ExecuteReader();
            List<(ProjectRecord, TreeEntry)> list = [];
            while (r.Read())
            {
                int n = ProjectColumnCount;
                list.Add((ReadProject(r, 0), new TreeEntry(path, r.GetString(n), r.GetInt64(n + 1), r.GetInt64(n + 2))));
            }

            return list;
        }
    }

    public void SetTreeEntry(int projectId, string path, string contentHash, long mtime, long size)
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        lock (this._gate)
        {
            using SqliteTransaction tx = this._connection.BeginTransaction();
            this.Execute("""
                INSERT INTO tree(project_id, path, content_hash, mtime, size, seen_at) VALUES ($p, $path, $h, $m, $s, $now)
                ON CONFLICT(project_id, path) DO UPDATE SET content_hash = excluded.content_hash, mtime = excluded.mtime, size = excluded.size, seen_at = excluded.seen_at
                """,
                ("$p", projectId), ("$path", path), ("$h", contentHash), ("$m", mtime), ("$s", size), ("$now", now));
            this.Execute("UPDATE blob SET last_used_at = $now WHERE project_id = $p AND content_hash = $h",
                ("$now", now), ("$p", projectId), ("$h", contentHash));
            tx.Commit();
        }
    }

    public void RemoveTreeEntries(int projectId, IEnumerable<string> paths)
    {
        lock (this._gate)
        {
            using SqliteTransaction tx = this._connection.BeginTransaction();
            foreach (string p in paths)
            {
                this.Execute("DELETE FROM tree WHERE project_id = $p AND path = $path", ("$p", projectId), ("$path", p));
            }

            tx.Commit();
        }
    }

    /// <summary>Removes the path from every project's tree (used when a file is deleted from disk).</summary>
    public int RemoveTreeEntriesByPath(string path)
    {
        lock (this._gate)
        {
            return this.Execute("DELETE FROM tree WHERE path = $path", ("$path", path));
        }
    }

    // ---------------------------------------------------------------- queries

    private const string TreeJoin = """
        FROM blob b
        JOIN tree t ON t.project_id = b.project_id AND t.content_hash = b.content_hash
        JOIN project p ON p.id = b.project_id
        """;

    /// <summary>
    /// Usages of a symbol in the current working tree. The enclosing declaration is joined from the same blob's symbol
    /// rows (the member, then its containing type), so the join stays inside the <c>blob</c> to <c>tree</c> branch scope.
    /// The usage kinds of <paramref name="filter"/> go into the query; its project scope is a name and path rule
    /// (<see cref="ProjectKinds.IsTestProject"/>) and is applied to the rows as they are read, so <paramref name="limit"/>
    /// counts the hits that pass.
    /// </summary>
    public IReadOnlyList<ReferenceHit> FindReferences(string targetDocId, string? targetAssembly = null, int limit = 500, ReferenceFilter? filter = null)
    {
        filter ??= ReferenceFilter.None;
        lock (this._gate)
        {
            using SqliteCommand cmd = this._connection.CreateCommand();
            string kindClause = "";
            if (filter.Kinds is not null)
            {
                List<string> names = [];
                foreach (string kind in filter.Kinds)
                {
                    string name = "$kind" + names.Count;
                    names.Add(name);
                    cmd.Parameters.AddWithValue(name, kind);
                }

                kindClause = $"AND r.kind IN ({string.Join(", ", names)})";
            }

            bool limitInSql = filter.Scope == ReferenceScope.All;
            cmd.CommandText = $"""
                SELECT p.name, p.solution_path, t.path, r.line, r.col, r.kind, t.content_hash, r.from_doc_id, s.name, c.name
                FROM reference r
                JOIN blob b ON b.id = r.blob_id
                JOIN tree t ON t.project_id = b.project_id AND t.content_hash = b.content_hash
                JOIN project p ON p.id = b.project_id
                LEFT JOIN symbol s ON s.blob_id = r.blob_id AND s.doc_id = r.from_doc_id
                LEFT JOIN symbol c ON c.blob_id = r.blob_id AND c.doc_id = s.containing_doc_id
                WHERE r.target_doc_id = $doc {(targetAssembly is null ? "" : "AND r.target_assembly = $asm")} {kindClause}
                ORDER BY p.name, t.path, r.line, r.col
                {(limitInSql ? "LIMIT $limit" : "")}
                """;
            cmd.Parameters.AddWithValue("$doc", targetDocId);
            if (limitInSql)
            {
                cmd.Parameters.AddWithValue("$limit", limit);
            }

            if (targetAssembly is not null)
            {
                cmd.Parameters.AddWithValue("$asm", targetAssembly);
            }

            using SqliteDataReader r = cmd.ExecuteReader();
            List<ReferenceHit> hits = [];
            while (hits.Count < limit && r.Read())
            {
                if (!filter.MatchesScope(r.GetString(0), r.GetString(2)))
                {
                    continue;
                }

                string? fromDocId = r.IsDBNull(7) ? null : r.GetString(7);
                string? enclosing = null;
                if (fromDocId == EnclosingDisplay.TopLevel)
                {
                    enclosing = EnclosingDisplay.TopLevel;
                }
                else if (!r.IsDBNull(8))
                {
                    enclosing = EnclosingDisplay.Format(r.IsDBNull(9) ? null : r.GetString(9), r.GetString(8));
                }

                hits.Add(new ReferenceHit(r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt32(3), r.GetInt32(4), r.GetString(5), r.GetString(6), fromDocId, enclosing));
            }

            return hits;
        }
    }

    /// <summary>Every current usage of a symbol counted per solution path, with no filter; the "of M" behind a filtered <see cref="FindReferences"/>.</summary>
    public IReadOnlyDictionary<string, int> CountReferencesBySolution(string targetDocId, string? targetAssembly = null)
    {
        lock (this._gate)
        {
            using SqliteCommand cmd = this._connection.CreateCommand();
            cmd.CommandText = $"""
                SELECT p.solution_path, COUNT(*)
                FROM reference r
                JOIN blob b ON b.id = r.blob_id
                JOIN tree t ON t.project_id = b.project_id AND t.content_hash = b.content_hash
                JOIN project p ON p.id = b.project_id
                WHERE r.target_doc_id = $doc {(targetAssembly is null ? "" : "AND r.target_assembly = $asm")}
                GROUP BY p.solution_path
                """;
            cmd.Parameters.AddWithValue("$doc", targetDocId);
            if (targetAssembly is not null)
            {
                cmd.Parameters.AddWithValue("$asm", targetAssembly);
            }

            Dictionary<string, int> counts = new(StringComparer.OrdinalIgnoreCase);
            using SqliteDataReader r = cmd.ExecuteReader();
            while (r.Read())
            {
                counts[r.GetString(0)] = r.GetInt32(1);
            }

            return counts;
        }
    }

    /// <summary>Every declaration the current tree records for a file path, in source order; one group per project that compiles the file.</summary>
    public IReadOnlyList<FileSymbolHit> GetFileSymbols(string path)
    {
        lock (this._gate)
        {
            using SqliteCommand cmd = this._connection.CreateCommand();
            cmd.CommandText = """
                SELECT p.name, p.solution_path, t.path, s.doc_id, s.kind, s.name, s.fq_name, s.signature, s.accessibility, s.containing_doc_id, s.line, s.col, s.end_line, t.content_hash
                FROM tree t
                JOIN blob b ON b.project_id = t.project_id AND b.content_hash = t.content_hash
                JOIN project p ON p.id = t.project_id
                JOIN symbol s ON s.blob_id = b.id
                WHERE t.path = $path
                ORDER BY p.name, s.line, s.col
                """;
            cmd.Parameters.AddWithValue("$path", path);
            using SqliteDataReader r = cmd.ExecuteReader();
            List<FileSymbolHit> hits = [];
            while (r.Read())
            {
                SymbolRecord symbol = new(
                    r.GetString(3), r.GetString(4), r.GetString(5), r.GetString(6), r.GetString(7), r.GetString(8),
                    r.IsDBNull(9) ? null : r.GetString(9), r.GetInt32(10), r.GetInt32(11), r.GetInt32(12));
                hits.Add(new FileSymbolHit(r.GetString(0), r.GetString(1), r.GetString(2), symbol, r.GetString(13)));
            }

            return hits;
        }
    }

    public IReadOnlyList<RelationHit> FindRelations(string toDocId, IReadOnlyCollection<string> kinds, int limit = 500)
    {
        lock (this._gate)
        {
            using SqliteCommand cmd = this._connection.CreateCommand();
            string kindList = string.Join(",", kinds.Select((_, i) => $"$k{i}"));
            cmd.CommandText = $"""
                SELECT p.name, p.solution_path, rel.from_doc_id, rel.kind, t.path, s.line, s.col, s.signature, t.content_hash
                FROM relation rel
                JOIN blob b ON b.id = rel.blob_id
                JOIN tree t ON t.project_id = b.project_id AND t.content_hash = b.content_hash
                JOIN project p ON p.id = b.project_id
                LEFT JOIN symbol s ON s.blob_id = rel.blob_id AND s.doc_id = rel.from_doc_id
                WHERE rel.to_doc_id = $doc AND rel.kind IN ({kindList})
                ORDER BY p.name, t.path, s.line
                LIMIT $limit
                """;
            cmd.Parameters.AddWithValue("$doc", toDocId);
            cmd.Parameters.AddWithValue("$limit", limit);
            int i = 0;
            foreach (string k in kinds)
            {
                cmd.Parameters.AddWithValue($"$k{i++}", k);
            }

            using SqliteDataReader r = cmd.ExecuteReader();
            List<RelationHit> hits = [];
            while (r.Read())
            {
                hits.Add(new RelationHit(
                    r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3),
                    r.IsDBNull(4) ? null : r.GetString(4),
                    r.IsDBNull(5) ? 0 : r.GetInt32(5),
                    r.IsDBNull(6) ? 0 : r.GetInt32(6),
                    r.IsDBNull(7) ? null : r.GetString(7),
                    r.GetString(8)));
            }

            return hits;
        }
    }

    public IReadOnlyList<SymbolHit> FindSymbolsByDocId(string docId, int limit = 50)
    {
        return this.QuerySymbols("s.doc_id = $q", docId, limit);
    }

    public IReadOnlyList<SymbolHit> FindSymbolsByName(string name, int limit = 50)
    {
        return this.QuerySymbols("(s.fq_name = $q COLLATE NOCASE OR s.name = $q COLLATE NOCASE)", name, limit);
    }

    /// <summary>
    /// Candidates from three matchers (FTS5 prefix on every term, LIKE substring and GLOB camel humps on the last term)
    /// ranked by <see cref="SymbolRanker"/>: match quality, kind, test project, name length, full name.
    /// </summary>
    public IReadOnlyList<SymbolHit> SearchSymbols(string query, string? kind = null, int limit = 50)
    {
        string[] terms = SymbolRanker.Terms(query);
        if (terms.Length == 0)
        {
            return [];
        }

        string namePart = terms[^1];
        string[] containers = terms[..^1];
        string fts = string.Join(" AND ", terms.Select(t => "\"" + t.Replace("\"", "\"\"") + "\"*"));
        string kindFilter = kind is null ? "" : " AND s.kind = $kind";
        string containerFilter = string.Concat(containers.Select((_, i) => $" AND s.fq_name LIKE $c{i} ESCAPE '\\'"));
        (string, object)[] containerParameters = containers.Select((c, i) => ($"$c{i}", (object)("%" + EscapeLike(c) + "%"))).ToArray();
        string? hump = HumpGlob(namePart);

        HashSet<SymbolHit> candidates = [];
        lock (this._gate)
        {
            candidates.UnionWith(this.ReadSymbols(
                SymbolSelect("symbol_fts f JOIN symbol s ON s.id = f.rowid", $"symbol_fts MATCH $q{kindFilter}"),
                [("$q", fts), ("$limit", limit), ("$kind", kind ?? "")]));
            candidates.UnionWith(this.ReadSymbols(
                SymbolSelect("symbol s", $"s.name LIKE $q ESCAPE '\\'{kindFilter}{containerFilter}"),
                [("$q", "%" + EscapeLike(namePart) + "%"), ("$limit", limit), ("$kind", kind ?? ""), .. containerParameters]));
            if (hump is not null)
            {
                candidates.UnionWith(this.ReadSymbols(
                    SymbolSelect("symbol s", $"s.name GLOB $q{kindFilter}{containerFilter}"),
                    [("$q", hump), ("$limit", limit), ("$kind", kind ?? ""), .. containerParameters]));
            }
        }

        return candidates
            .Select(h => (Hit: h, Score: SymbolRanker.Score(namePart, h.Name, h.Kind, h.ProjectName, h.FilePath)))
            .Where(x => x.Score is not null && containers.All(c => x.Hit.FqName.Contains(c, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(x => x.Score!.Value)
            .ThenBy(x => x.Hit.FqName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Hit.FilePath, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.Hit)
            .Take(limit)
            .ToList();
    }

    /// <summary>Candidate query for <see cref="SearchSymbols"/>; the pre-cut order approximates the final ranking so the LIMIT drops the least likely rows.</summary>
    private static string SymbolSelect(string from, string where)
    {
        return $"""
            SELECT p.name, p.solution_path, t.path, s.doc_id, s.kind, s.name, s.fq_name, s.signature, s.line, s.col, t.content_hash
            FROM {from}
            JOIN blob b ON b.id = s.blob_id
            JOIN tree t ON t.project_id = b.project_id AND t.content_hash = b.content_hash
            JOIN project p ON p.id = b.project_id
            WHERE {where}
            ORDER BY CASE s.kind
                WHEN 'class' THEN 0 WHEN 'interface' THEN 0 WHEN 'record' THEN 0 WHEN 'record struct' THEN 0 WHEN 'struct' THEN 0 WHEN 'enum' THEN 0 WHEN 'delegate' THEN 0
                WHEN 'method' THEN 0 WHEN 'constructor' THEN 0 WHEN 'operator' THEN 0
                WHEN 'property' THEN 1 WHEN 'indexer' THEN 1 WHEN 'event' THEN 1
                ELSE 2 END, length(s.name), s.fq_name
            LIMIT $limit
            """;
    }

    private static string EscapeLike(string value)
    {
        return value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
    }

    /// <summary>A case-sensitive GLOB such as <c>*Up*Ca*Co*</c> for a pattern with at least two humps; null for a single-hump pattern, which the substring query covers.</summary>
    private static string? HumpGlob(string pattern)
    {
        List<int> starts = SymbolRanker.PatternHumpStarts(pattern);
        if (starts.Count < 2)
        {
            return null;
        }

        StringBuilder sb = new("*");
        for (int i = 0; i < starts.Count; i++)
        {
            int end = i + 1 < starts.Count ? starts[i + 1] : pattern.Length;
            foreach (char c in pattern.AsSpan(starts[i], end - starts[i]).TrimEnd('_'))
            {
                sb.Append(c is '*' or '?' or '[' ? "[" + c + "]" : c.ToString());
            }

            sb.Append('*');
        }

        return sb.ToString();
    }

    private IReadOnlyList<SymbolHit> QuerySymbols(string where, string value, int limit)
    {
        lock (this._gate)
        {
            return this.ReadSymbols(
                $"""
                SELECT p.name, p.solution_path, t.path, s.doc_id, s.kind, s.name, s.fq_name, s.signature, s.line, s.col, t.content_hash
                FROM symbol s
                JOIN blob b ON b.id = s.blob_id
                JOIN tree t ON t.project_id = b.project_id AND t.content_hash = b.content_hash
                JOIN project p ON p.id = b.project_id
                WHERE {where}
                ORDER BY p.name, t.path, s.line
                LIMIT $limit
                """,
                [("$q", value), ("$limit", limit)]);
        }
    }

    private List<SymbolHit> ReadSymbols(string sql, (string, object)[] parameters)
    {
        using SqliteCommand cmd = this._connection.CreateCommand();
        cmd.CommandText = sql;
        foreach ((string name, object value) in parameters)
        {
            cmd.Parameters.AddWithValue(name, value);
        }

        using SqliteDataReader r = cmd.ExecuteReader();
        List<SymbolHit> hits = [];
        while (r.Read())
        {
            hits.Add(new SymbolHit(
                r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4),
                r.GetString(5), r.GetString(6), r.GetString(7), r.GetInt32(8), r.GetInt32(9), r.GetString(10)));
        }

        return hits;
    }

    // ---------------------------------------------------------------- maintenance

    /// <summary>Deletes blobs that no tree entry points at and that have not been used for <paramref name="retention"/>.</summary>
    public int CollectGarbage(TimeSpan retention)
    {
        long cutoff = DateTimeOffset.UtcNow.Subtract(retention).ToUnixTimeSeconds();
        lock (this._gate)
        {
            using SqliteTransaction tx = this._connection.BeginTransaction();
            List<long> victims = [];
            using (SqliteCommand cmd = this._connection.CreateCommand())
            {
                cmd.CommandText = """
                    SELECT b.id FROM blob b
                    WHERE b.last_used_at < $cutoff
                      AND NOT EXISTS (SELECT 1 FROM tree t WHERE t.project_id = b.project_id AND t.content_hash = b.content_hash)
                    """;
                cmd.Parameters.AddWithValue("$cutoff", cutoff);
                using SqliteDataReader r = cmd.ExecuteReader();
                while (r.Read())
                {
                    victims.Add(r.GetInt64(0));
                }
            }

            foreach (long id in victims)
            {
                this.DeleteBlobRows(id);
                this.Execute("DELETE FROM blob WHERE id = $b", ("$b", id));
            }

            tx.Commit();
            return victims.Count;
        }
    }

    public IndexStats GetStats()
    {
        lock (this._gate)
        {
            return new IndexStats(
                (int)this.ScalarLong("SELECT COUNT(*) FROM project")!.Value,
                (int)this.ScalarLong("SELECT COUNT(*) FROM blob")!.Value,
                (int)this.ScalarLong("SELECT COUNT(*) FROM symbol")!.Value,
                (int)this.ScalarLong("SELECT COUNT(*) FROM reference")!.Value,
                (int)this.ScalarLong("SELECT COUNT(*) FROM relation")!.Value,
                (int)this.ScalarLong("SELECT COUNT(*) FROM tree")!.Value);
        }
    }

    // ---------------------------------------------------------------- helpers

    private int Execute(string sql, params (string, object)[] parameters)
    {
        using SqliteCommand cmd = this._connection.CreateCommand();
        cmd.CommandText = sql;
        foreach ((string name, object value) in parameters)
        {
            cmd.Parameters.AddWithValue(name, value);
        }

        return cmd.ExecuteNonQuery();
    }

    private long? ScalarLong(string sql, params (string, object)[] parameters)
    {
        using SqliteCommand cmd = this._connection.CreateCommand();
        cmd.CommandText = sql;
        foreach ((string name, object value) in parameters)
        {
            cmd.Parameters.AddWithValue(name, value);
        }

        object? result = cmd.ExecuteScalar();
        return result is null or DBNull ? null : Convert.ToInt64(result);
    }

    public void Dispose()
    {
        this._connection.Dispose();
    }
}
