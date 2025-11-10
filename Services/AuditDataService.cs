using CFACalculateWebAPI.Data;
using CFACalculateWebAPI.Models;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace CFACalculateWebAPI.Services
{
    public class AuditDataService
    {
        public class TimedFinalFillResult
        {
            public List<double> TimedFills { get; set; } = new List<double>();
            public List<double> FinalFills { get; set; } = new List<double>();
            public List<int> MainFillIndicators { get; set; } = new List<int>();
        }

        private readonly AppDbContext _context;

        public AuditDataService(AppDbContext context)
        {
            _context = context; // ✅ injected DbContext ensures connection string is set
        }

        /// <summary>
        /// Init Sample Run Numbers
        /// </summary>
        public async Task<List<SampleRunResult>> InitSampleRunNoAsync(string? serial, string? auditId)
        {
            var results = new List<SampleRunResult>();

            // Build SQL like Delphi
            var sql = @"
WITH dat1 AS (
    SELECT ROW_NUMBER() OVER (ORDER BY cfa_data_excel.seconds ASC) - 1 AS SampleRun,
           seconds,
           IIF(CAST(waterusage AS FLOAT) > (LAG(CAST(waterusage AS FLOAT)) OVER (ORDER BY seconds) + 10), 1, 0) AS ProductFilling
    FROM cfa_data_excel
    WHERE auditid = {0}
),
dat2 AS (
    SELECT samplerun,
           seconds,
           IIF(productfilling > LAG(productfilling) OVER (ORDER BY seconds), 1, 0) AS FILLSTART,
           IIF(productfilling < LAG(productfilling) OVER (ORDER BY seconds), 1, 0) AS FILLEND
    FROM dat1
),
datfillstart AS (
    SELECT ROW_NUMBER() OVER (ORDER BY dat1.seconds ASC) AS RunNo,
           fillstart,
           fillend,
           dat1.seconds,
           dat2.samplerun
    FROM dat1, dat2
    WHERE dat1.seconds = dat2.seconds AND dat2.fillstart = 1
),
datfillend AS (
    SELECT ROW_NUMBER() OVER (ORDER BY dat1.seconds ASC) AS RunNo,
           fillstart,
           fillend,
           dat1.seconds,
           dat2.samplerun
    FROM dat1, dat2
    WHERE dat1.seconds = dat2.seconds AND dat2.fillend = 1
)
SELECT ds.RunNo, ds.samplerun AS StartSampleRun, de.samplerun AS EndSampleRun
FROM datfillstart ds
INNER JOIN datfillend de ON ds.RunNo = de.RunNo;";

            // Decide audit condition
            string auditCondition;
            bool useAuditId = !string.IsNullOrWhiteSpace(auditId);

            if (useAuditId)
                auditCondition = "@AuditId";
            else
            {
                if (string.IsNullOrWhiteSpace(serial))
                    throw new ArgumentException("Serial cannot be empty when AuditId is not provided.");

                auditCondition = "(SELECT TOP 1 AuditID FROM Audit WHERE Serial = @Serial ORDER BY AuditID DESC)";
            }

            sql = string.Format(sql, auditCondition);

            using var conn = _context.Database.GetDbConnection();
            if (conn.State != System.Data.ConnectionState.Open)
                await conn.OpenAsync();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;

            if (useAuditId)
                cmd.Parameters.Add(new SqlParameter("@AuditId", auditId!));
            else
                cmd.Parameters.Add(new SqlParameter("@Serial", serial!));

            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                results.Add(new SampleRunResult
                {
                    RunNo = Convert.ToInt32(reader.GetValue(0)),
                    StartSampleRun = Convert.ToInt32(reader.GetValue(1)),
                    EndSampleRun = Convert.ToInt32(reader.GetValue(2))
                });
            }

            return results;
        }

        /// <summary>
        /// Calculate Timed Final Fills
        /// </summary>
        public async Task<TimedFinalFillResult> CalTimedFinalFillsNAsync(string? serial, string? auditId, List<int> endSampleNos)
        {
            var timedFills = new List<double>();

            if (endSampleNos == null || endSampleNos.Count == 0)
                throw new ArgumentException("endSampleNos cannot be empty.");

            var sql = $@"
WITH basedata AS (
    SELECT ROW_NUMBER() OVER (ORDER BY seconds ASC) AS SampleRun,
           seconds,
           voltage,
           [current],
           [power],
           powerusage,
           waterusage,
           temperature,
           waterpressure,
           watertemperature
    FROM cfa_data_excel
    WHERE auditid = {(string.IsNullOrWhiteSpace(auditId) ?
        $"(SELECT TOP 1 AuditID FROM Audit WHERE Serial = @Serial ORDER BY AuditID DESC)" : "@AuditId")}
),
timedfills AS (
    SELECT CAST(waterusage AS FLOAT) * 
           (-0.00000001 * POWER(CAST(watertemperature AS FLOAT), 3) +
            0.000006 * POWER(CAST(watertemperature AS FLOAT), 2) +
           -0.00002 * CAST(watertemperature AS FLOAT) + 1) / 1000 AS FILLS,
           SampleRun
    FROM basedata
   WHERE SampleRun IN (" + string.Join(",", endSampleNos) + @")
)
SELECT FILLS
FROM timedfills
ORDER BY FILLS;";

            using var conn = _context.Database.GetDbConnection();
            conn.ConnectionString = "Server=redbow;Database=Thailis;User Id=thrftest;Password=thrftest;TrustServerCertificate=True;";
            if (conn.State != System.Data.ConnectionState.Open)
                await conn.OpenAsync();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;

            if (!string.IsNullOrWhiteSpace(auditId))
                cmd.Parameters.Add(new SqlParameter("@AuditId", auditId!));
            else
                cmd.Parameters.Add(new SqlParameter("@Serial", serial!));

            using var reader = await cmd.ExecuteReaderAsync();
            double tempResult = 0;
            int i = 0;
            while (await reader.ReadAsync())
            {
                double value = reader.GetDouble(0);

                if (i == 0)
                {
                    tempResult = value;
                    timedFills.Add(tempResult);
                }
                else
                {
                    double delta = value - tempResult;
                    timedFills.Add(delta);
                    tempResult = value;
                }

                i++;
            }

            // ===== Calculate Final Fills based on Delphi logic =====
            var finalFillResult = CalculateFinalFills(timedFills);

            return finalFillResult;
        }

        private TimedFinalFillResult CalculateFinalFills(List<double> timedFills)
        {
            int n = timedFills.Count;
            var RetTimedFills = new List<double>(new double[n]);
            var RetFinalFills = new List<double>(new double[n]);
            var gRunNo = new List<int>(new int[n]);

            int j = 0;

            // Timed Fill
            for (int i = 0; i < n; i++)
            {
                gRunNo[i] = 0;
                if (timedFills[i] > 1) // >1 = Main Fill
                {
                    RetTimedFills[j] = timedFills[i];
                    gRunNo[i] = 1;
                    j++;
                }
            }

            // Final Fill
            j = 0;
            for (int i = 0; i < n - 1; i++)
            {
                if (timedFills[i] > 1)
                {
                    RetFinalFills[j] = timedFills[i] + timedFills[i + 1];
                    j++;
                }
            }

            return new TimedFinalFillResult
            {
                TimedFills = RetTimedFills,
                FinalFills = RetFinalFills,
                MainFillIndicators = gRunNo
            };
        }

        /// <summary>
        /// Calculate FVFR (converted from Delphi calFVFRN)
        /// </summary>
        public async Task<double> CalFVFRNAsync(string? serial, string? auditId, int mainFillTimes, List<(int start, int end)> mainFillRanges)
        {
            double[] fvfrIn = new double[mainFillTimes];

            using var conn = _context.Database.GetDbConnection();
            conn.ConnectionString = "Server=redbow;Database=Thailis;User Id=thrftest;Password=thrftest;TrustServerCertificate=True;";
            if (conn.State != System.Data.ConnectionState.Open)
                await conn.OpenAsync();

            for (int i = 0; i < mainFillTimes; i++)
            {
                int startNo = mainFillRanges[i].start + 4;
                int endNo = mainFillRanges[i].end - 3;
                string sampleRange = $"({startNo},{endNo})";

                string sql = $@"
WITH basedata AS (
    SELECT ROW_NUMBER() OVER (ORDER BY seconds ASC) AS SampleRun,
           seconds, voltage, [current], [power],
           powerusage, waterusage, temperature, waterpressure, watertemperature
    FROM cfa_data_excel
    WHERE auditid = {(string.IsNullOrEmpty(auditId)
        ? $"(SELECT TOP 1 AuditID FROM Audit WHERE Serial = '{serial}' ORDER BY AuditID DESC)"
        : $"'{auditId}'")}
),
FVFR AS (
    SELECT 
        CAST(waterusage AS FLOAT) * (
            -0.00000001 * POWER(CAST(watertemperature AS FLOAT), 3) +
             0.000006 * POWER(CAST(watertemperature AS FLOAT), 2) +
            -0.00002 * CAST(watertemperature AS FLOAT) + 1
        ) AS TempComp,
        basedata.seconds,
        basedata.SampleRun
    FROM basedata
    WHERE basedata.SampleRun IN {sampleRange}
)
SELECT FVFR.TempComp, FVFR.seconds
FROM FVFR
ORDER BY FVFR.seconds ASC;
";

                using var cmd = conn.CreateCommand();
                cmd.CommandText = sql;

                using var reader = await cmd.ExecuteReaderAsync();
                double? firstTemp = null, firstSec = null;
                double? secondTemp = null, secondSec = null;
                int j = 0;

                while (await reader.ReadAsync())
                {
                    double temp = reader.GetDouble(0);
                    double sec = reader.GetDouble(1);

                    if (j == 0)
                    {
                        firstTemp = temp;
                        firstSec = sec;
                    }
                    else if (j == 1)
                    {
                        secondTemp = temp;
                        secondSec = sec;

                        if (firstTemp.HasValue && secondTemp.HasValue && firstSec.HasValue && secondSec.HasValue)
                        {
                            fvfrIn[i] = (secondTemp.Value - firstTemp.Value) / (secondSec.Value - firstSec.Value);
                        }
                    }

                    j++;
                }

                reader.Close();
            }

            // Average FVFR across all fills
            return fvfrIn.Average();
        }
        

        /// <summary>
/// Calculate average water temperature with compensation (converted from Delphi calInComWTemp)
/// </summary>
public async Task<double> CalInComWTempAsync(string? serial, string? auditId, int mainFillTimes, List<(int start, int end)> mainFillRanges)
{
    using var conn = _context.Database.GetDbConnection();
    conn.ConnectionString = "Server=redbow;Database=Thailis;User Id=thrftest;Password=thrftest;TrustServerCertificate=True;";
    if (conn.State != System.Data.ConnectionState.Open)
        await conn.OpenAsync();

    var temps = new List<double>();

    for (int i = 0; i < mainFillTimes; i++)
    {
        int startNo = mainFillRanges[i].start + 1; // Delphi adds +1
        int endNo = mainFillRanges[i].end + 1;
        string sampleRange = $"{startNo},{endNo}";

        string sql = $@"
WITH basedata AS (
    SELECT ROW_NUMBER() OVER (ORDER BY seconds ASC) AS SampleRun,
           seconds, voltage, [current], [power],
           powerusage, waterusage, temperature, waterpressure, watertemperature
    FROM cfa_data_excel
    WHERE auditid = {(string.IsNullOrEmpty(auditId)
        ? $"(SELECT TOP 1 AuditID FROM Audit WHERE Serial = '{serial}' ORDER BY AuditID DESC)"
        : $"'{auditId}'")}
),
FVFR AS (
    SELECT CAST(waterusage AS FLOAT) * (
        -0.00000001 * POWER(CAST(watertemperature AS FLOAT), 3) +
         0.000006 * POWER(CAST(watertemperature AS FLOAT), 2) +
        -0.00002 * CAST(watertemperature AS FLOAT) + 1
    ) AS TempComp,
    basedata.seconds,
    basedata.WaterTemperature,
    basedata.SampleRun
    FROM basedata
    WHERE (basedata.SampleRun BETWEEN {startNo} AND {endNo})
)
SELECT AVG(CAST(watertemperature AS FLOAT))
FROM FVFR;";

        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;

        var result = await cmd.ExecuteScalarAsync();
        if (result != DBNull.Value && result != null)
            temps.Add(Convert.ToDouble(result));
    }

    // Average across all main fills
    return temps.Count > 0 ? temps.Average() : 0;
}

    }
}
