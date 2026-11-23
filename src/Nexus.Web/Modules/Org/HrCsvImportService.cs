using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Nexus.Web.Infrastructure.Data;
using Nexus.Web.Infrastructure.Security;

namespace Nexus.Web.Modules.Org;

public sealed record HrImportRowChange(
    string EmployeeCode,
    string FullName,
    string Action,
    string? Detail = null);

public sealed record UnverifiedMatchSuggestion(
    Guid UserId,
    string Username,
    string DisplayName,
    Guid EmployeeId,
    string EmployeeCode,
    string EmployeeFullName,
    string Confidence,
    string Reason);

public sealed record HrCsvImportReport(
    bool DryRun,
    int TotalRows,
    int AddedCount,
    int UpdatedCount,
    int UnchangedCount,
    int DeactivatedCount,
    int DepartmentsCreated,
    int BranchesCreated,
    IReadOnlyList<string> Errors,
    IReadOnlyList<HrImportRowChange> Diff,
    IReadOnlyList<UnverifiedMatchSuggestion> MatchSuggestions);

public static class HrCsvImportService
{
    public static async Task<HrCsvImportReport> ImportAsync(
        NexusDbContext db,
        Guid organizationId,
        string csvContent,
        bool dryRun,
        CancellationToken ct = default)
    {
        var errors = new List<string>();
        var parsedRows = ParseCsv(csvContent, errors);

        if (errors.Count > 0)
        {
            return new HrCsvImportReport(dryRun, parsedRows.Count, 0, 0, 0, 0, 0, 0, errors, [], []);
        }

        var existingDepts = await db.Departments
            .Where(d => d.OrganizationId == organizationId)
            .ToDictionaryAsync(d => d.Code, StringComparer.OrdinalIgnoreCase, ct);

        var existingBranches = await db.Branches
            .Where(b => b.OrganizationId == organizationId)
            .ToDictionaryAsync(b => b.Code, StringComparer.OrdinalIgnoreCase, ct);

        var existingEmployees = await db.Employees
            .Where(e => e.OrganizationId == organizationId)
            .ToDictionaryAsync(e => e.EmployeeCode, StringComparer.OrdinalIgnoreCase, ct);

        var now = DateTimeOffset.UtcNow;
        var diff = new List<HrImportRowChange>();
        var added = 0;
        var updated = 0;
        var unchanged = 0;
        var deactivated = 0;
        var deptsCreated = 0;
        var branchesCreated = 0;

        // Track virtual/persisted employee state for accurate match suggestions even during dry-run.
        var simulatedEmployees = existingEmployees.Values.ToDictionary(
            e => e.EmployeeCode,
            e => (e.Id, e.EmployeeCode, e.FullName, e.IsActive),
            StringComparer.OrdinalIgnoreCase);

        foreach (var row in parsedRows)
        {
            Guid? deptId = null;
            if (!string.IsNullOrWhiteSpace(row.DepartmentCode))
            {
                if (!existingDepts.TryGetValue(row.DepartmentCode, out var dept))
                {
                    dept = new DepartmentEntity
                    {
                        Id = Guid.CreateVersion7(),
                        OrganizationId = organizationId,
                        Code = row.DepartmentCode,
                        Name = string.IsNullOrWhiteSpace(row.DepartmentName) ? row.DepartmentCode : row.DepartmentName,
                        CreatedAt = now
                    };
                    existingDepts[row.DepartmentCode] = dept;
                    deptsCreated++;
                    if (!dryRun)
                    {
                        db.Departments.Add(dept);
                    }
                }
                else if (!string.IsNullOrWhiteSpace(row.DepartmentName) &&
                         !string.Equals(dept.Name, row.DepartmentName, StringComparison.Ordinal))
                {
                    if (!dryRun)
                    {
                        dept.Name = row.DepartmentName;
                    }
                }

                deptId = dept.Id;
            }

            Guid? branchId = null;
            if (!string.IsNullOrWhiteSpace(row.BranchCode))
            {
                if (!existingBranches.TryGetValue(row.BranchCode, out var branch))
                {
                    branch = new BranchEntity
                    {
                        Id = Guid.CreateVersion7(),
                        OrganizationId = organizationId,
                        Code = row.BranchCode,
                        Name = string.IsNullOrWhiteSpace(row.BranchName) ? row.BranchCode : row.BranchName,
                        CreatedAt = now
                    };
                    existingBranches[row.BranchCode] = branch;
                    branchesCreated++;
                    if (!dryRun)
                    {
                        db.Branches.Add(branch);
                    }
                }
                else if (!string.IsNullOrWhiteSpace(row.BranchName) &&
                         !string.Equals(branch.Name, row.BranchName, StringComparison.Ordinal))
                {
                    if (!dryRun)
                    {
                        branch.Name = row.BranchName;
                    }
                }

                branchId = branch.Id;
            }

            if (!existingEmployees.TryGetValue(row.EmployeeCode, out var emp))
            {
                var newEmp = new EmployeeEntity
                {
                    Id = Guid.CreateVersion7(),
                    OrganizationId = organizationId,
                    EmployeeCode = row.EmployeeCode,
                    FullName = row.FullName,
                    Email = row.Email,
                    DepartmentId = deptId,
                    BranchId = branchId,
                    IsActive = row.IsActive,
                    CreatedAt = now,
                    UpdatedAt = now
                };
                existingEmployees[row.EmployeeCode] = newEmp;
                simulatedEmployees[row.EmployeeCode] = (newEmp.Id, newEmp.EmployeeCode, newEmp.FullName, newEmp.IsActive);
                added++;
                diff.Add(new HrImportRowChange(row.EmployeeCode, row.FullName, "Added"));

                if (!dryRun)
                {
                    db.Employees.Add(newEmp);
                }
            }
            else
            {
                var changes = new List<string>();
                if (!string.Equals(emp.FullName, row.FullName, StringComparison.Ordinal)) changes.Add("full_name");
                if (!string.Equals(emp.Email ?? string.Empty, row.Email ?? string.Empty, StringComparison.OrdinalIgnoreCase)) changes.Add("email");
                if (emp.DepartmentId != deptId) changes.Add("department");
                if (emp.BranchId != branchId) changes.Add("branch");
                if (emp.IsActive != row.IsActive) changes.Add("is_active");

                if (changes.Count == 0)
                {
                    unchanged++;
                }
                else
                {
                    var wasActive = emp.IsActive;
                    if (!dryRun)
                    {
                        emp.FullName = row.FullName;
                        emp.Email = row.Email;
                        emp.DepartmentId = deptId;
                        emp.BranchId = branchId;
                        emp.IsActive = row.IsActive;
                        emp.UpdatedAt = now;
                    }

                    simulatedEmployees[row.EmployeeCode] = (emp.Id, emp.EmployeeCode, row.FullName, row.IsActive);
                    if (wasActive && !row.IsActive)
                    {
                        deactivated++;
                        diff.Add(new HrImportRowChange(row.EmployeeCode, row.FullName, "Deactivated", string.Join(", ", changes)));
                    }
                    else
                    {
                        updated++;
                        diff.Add(new HrImportRowChange(row.EmployeeCode, row.FullName, "Updated", string.Join(", ", changes)));
                    }
                }
            }
        }

        if (!dryRun)
        {
            await db.SaveChangesAsync(ct);
        }

        var suggestions = await BuildMatchSuggestionsAsync(db, organizationId, simulatedEmployees.Values, ct);

        return new HrCsvImportReport(
            dryRun,
            parsedRows.Count,
            added,
            updated,
            unchanged,
            deactivated,
            deptsCreated,
            branchesCreated,
            errors,
            diff,
            suggestions);
    }

    public static async Task<IReadOnlyList<UnverifiedMatchSuggestion>> GetMatchSuggestionsAsync(
        NexusDbContext db,
        Guid organizationId,
        CancellationToken ct = default)
    {
        var employees = await db.Employees
            .AsNoTracking()
            .Where(e => e.OrganizationId == organizationId && e.IsActive)
            .Select(e => new ValueTuple<Guid, string, string, bool>(e.Id, e.EmployeeCode, e.FullName, e.IsActive))
            .ToListAsync(ct);

        return await BuildMatchSuggestionsAsync(db, organizationId, employees, ct);
    }

    private static async Task<IReadOnlyList<UnverifiedMatchSuggestion>> BuildMatchSuggestionsAsync(
        NexusDbContext db,
        Guid organizationId,
        IEnumerable<(Guid Id, string EmployeeCode, string FullName, bool IsActive)> employees,
        CancellationToken ct)
    {
        var linkedEmployeeIds = await db.OrganizationPeople
            .AsNoTracking()
            .Where(u => u.OrganizationId == organizationId && u.EmployeeId != null)
            .Select(u => u.EmployeeId!.Value)
            .ToHashSetAsync(ct);

        var unverifiedUsers = await db.OrganizationPeople
            .AsNoTracking()
            .Where(u =>
                u.OrganizationId == organizationId &&
                u.Status == AccountStatus.Active &&
                u.TrustLevel == TrustLevel.Unverified &&
                u.EmployeeId == null)
            .OrderBy(u => u.CreatedAt)
            .Take(200)
            .Select(u => new { u.Id, Username = u.UserName ?? string.Empty, u.DisplayName })
            .ToListAsync(ct);

        var availableEmployees = employees
            .Where(e => e.IsActive && !linkedEmployeeIds.Contains(e.Id))
            .Select(e => new
            {
                e.Id,
                e.EmployeeCode,
                e.FullName,
                Normalized = NormalizeVietnamese(e.FullName),
                Tokens = Tokenize(NormalizeVietnamese(e.FullName))
            })
            .ToList();

        var suggestions = new List<UnverifiedMatchSuggestion>();
        foreach (var user in unverifiedUsers)
        {
            var userNorm = NormalizeVietnamese(user.DisplayName);
            if (string.IsNullOrEmpty(userNorm))
            {
                continue;
            }

            var exact = availableEmployees.FirstOrDefault(e => string.Equals(e.Normalized, userNorm, StringComparison.Ordinal));
            if (exact is not null)
            {
                suggestions.Add(new UnverifiedMatchSuggestion(
                    user.Id, user.Username, user.DisplayName,
                    exact.Id, exact.EmployeeCode, exact.FullName,
                    "high", "Exact diacritic-insensitive name match"));
                continue;
            }

            var userTokens = Tokenize(userNorm);
            if (userTokens.Count < 2)
            {
                continue;
            }

            var partial = availableEmployees.FirstOrDefault(e =>
                e.Tokens.Count >= 2 &&
                userTokens.All(t => e.Tokens.Contains(t)));

            if (partial is not null)
            {
                suggestions.Add(new UnverifiedMatchSuggestion(
                    user.Id, user.Username, user.DisplayName,
                    partial.Id, partial.EmployeeCode, partial.FullName,
                    "medium", "All name tokens match employee record"));
            }

            if (suggestions.Count >= 50)
            {
                break;
            }
        }

        return suggestions;
    }

    public static string NormalizeVietnamese(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return string.Empty;
        }

        var formD = input.Trim().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(formD.Length);
        var prevSpace = false;

        foreach (var ch in formD)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            var mapped = ch switch
            {
                'đ' or 'Đ' => 'd',
                _ => char.ToLowerInvariant(ch)
            };

            if (char.IsWhiteSpace(mapped))
            {
                if (!prevSpace && sb.Length > 0)
                {
                    sb.Append(' ');
                    prevSpace = true;
                }
            }
            else
            {
                sb.Append(mapped);
                prevSpace = false;
            }
        }

        return sb.ToString().Trim();
    }

    private static HashSet<string> Tokenize(string normalized) =>
        normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);

    private sealed record ParsedHrRow(
        string EmployeeCode,
        string FullName,
        string? Email,
        string? DepartmentCode,
        string? DepartmentName,
        string? BranchCode,
        string? BranchName,
        bool IsActive);

    private static List<ParsedHrRow> ParseCsv(string csvContent, List<string> errors)
    {
        var results = new List<ParsedHrRow>();
        if (string.IsNullOrWhiteSpace(csvContent))
        {
            errors.Add("CSV content is empty.");
            return results;
        }

        var trimmed = csvContent.TrimStart('\uFEFF');
        var rawLines = SplitCsvRecords(trimmed);
        if (rawLines.Count < 2)
        {
            errors.Add("CSV must include a header row and at least one employee row.");
            return results;
        }

        var headers = rawLines[0].Select(h => h.Trim().ToLowerInvariant()).ToList();
        var colMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < headers.Count; i++)
        {
            if (!string.IsNullOrEmpty(headers[i]))
            {
                colMap[headers[i]] = i;
            }
        }

        if (!colMap.ContainsKey("employee_code") || !colMap.ContainsKey("full_name"))
        {
            errors.Add("CSV header must contain 'employee_code' and 'full_name' columns.");
            return results;
        }

        var seenCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var lineIdx = 1; lineIdx < rawLines.Count; lineIdx++)
        {
            var cols = rawLines[lineIdx];
            if (cols.All(string.IsNullOrWhiteSpace))
            {
                continue;
            }

            string? Get(string name) =>
                colMap.TryGetValue(name, out var idx) && idx < cols.Count
                    ? cols[idx].Trim()
                    : null;

            var code = (Get("employee_code") ?? string.Empty).ToUpperInvariant();
            var fullName = Get("full_name") ?? string.Empty;

            if (string.IsNullOrWhiteSpace(code) || code.Length > 64)
            {
                errors.Add($"Row {lineIdx + 1}: invalid or missing employee_code.");
                continue;
            }

            if (string.IsNullOrWhiteSpace(fullName) || fullName.Length > 160)
            {
                errors.Add($"Row {lineIdx + 1}: invalid or missing full_name for '{code}'.");
                continue;
            }

            if (!seenCodes.Add(code))
            {
                errors.Add($"Row {lineIdx + 1}: duplicate employee_code '{code}' inside CSV.");
                continue;
            }

            var email = Get("email");
            var deptCode = Get("department_code")?.ToUpperInvariant();
            var deptName = Get("department_name");
            var branchCode = Get("branch_code")?.ToUpperInvariant();
            var branchName = Get("branch_name");
            var activeRaw = Get("is_active");
            var isActive = string.IsNullOrWhiteSpace(activeRaw) ||
                           activeRaw.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                           activeRaw.Equals("1", StringComparison.OrdinalIgnoreCase) ||
                           activeRaw.Equals("active", StringComparison.OrdinalIgnoreCase);

            results.Add(new ParsedHrRow(
                code,
                fullName,
                string.IsNullOrWhiteSpace(email) ? null : email,
                string.IsNullOrWhiteSpace(deptCode) ? null : deptCode,
                string.IsNullOrWhiteSpace(deptName) ? null : deptName,
                string.IsNullOrWhiteSpace(branchCode) ? null : branchCode,
                string.IsNullOrWhiteSpace(branchName) ? null : branchName,
                isActive));
        }

        return results;
    }

    private static List<List<string>> SplitCsvRecords(string text)
    {
        var records = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        field.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    field.Append(c);
                }
            }
            else if (c == '"')
            {
                inQuotes = true;
            }
            else if (c == ',')
            {
                row.Add(field.ToString());
                field.Clear();
            }
            else if (c == '\r' || c == '\n')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }

                row.Add(field.ToString());
                field.Clear();
                if (row.Count > 1 || !string.IsNullOrEmpty(row[0]))
                {
                    records.Add(row);
                }
                row = [];
            }
            else
            {
                field.Append(c);
            }
        }

        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            records.Add(row);
        }

        return records;
    }
}
