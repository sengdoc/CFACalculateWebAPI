using CFACalculateWebAPI.Data;
using CFACalculateWebAPI.Models;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using System.Data.Common; // ✅ add this
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

private async Task<DbConnection> GetOpenConnectionAsync()
{
    var conn = _context.Database.GetDbConnection();

    if (conn.State != System.Data.ConnectionState.Open)
    {
        // Set connection string only if not set
        if (string.IsNullOrEmpty(conn.ConnectionString))
        {
            conn.ConnectionString = "Server=redbow;Database=Thailis;User Id=thrftest;Password=thrftest;TrustServerCertificate=True;";
        }
        await conn.OpenAsync();
    }

    return conn;
}


        // Init Sample Run Numbers        
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

            using var conn = await GetOpenConnectionAsync();


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

        // Calculate Timed Final Fills
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

            using var conn = await GetOpenConnectionAsync();

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

        // Calculate Final Fills
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

        // Calculate FVFR (converted from Delphi calFVFRN)
        public async Task<double> CalFVFRNAsync(string? serial, string? auditId, int mainFillTimes, List<(int start, int end)> mainFillRanges)
        {
            double[] fvfrIn = new double[mainFillTimes];

            using var conn = await GetOpenConnectionAsync();

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

        // Calculate average water temperature with compensation (converted from Delphi calInComWTemp)
        public async Task<double> CalInComWTempAsync(string? serial, string? auditId, int mainFillTimes, List<(int start, int end)> mainFillRanges)
        {
            using var conn = await GetOpenConnectionAsync();

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

        // Calculate heat-up rate (°C/s), converted from Delphi calHeatUpRate
        public async Task<double> CalHeatUpRateAsync(string? serial, string? auditId)
        {
            using var conn = await GetOpenConnectionAsync();

            string sql = $@"
WITH Dat1 AS (
    SELECT Seconds, power AS PowerUse, Temperature, Seconds AS CycleTSec
    FROM cfa_data_excel
    WHERE auditid = {(string.IsNullOrEmpty(auditId)
                ? $"(SELECT TOP 1 AuditID FROM Audit WHERE Serial = '{serial}' ORDER BY AuditID DESC)"
                : $"'{auditId}'")}
),
Dat2 AS (
    SELECT Seconds,
           IIF(CAST(PowerUse AS FLOAT) > 300, 1, 0) AS ProductHeating
    FROM Dat1
),
Dat3 AS (
    SELECT Seconds,
           IIF(ProductHeating > LAG(ProductHeating) OVER (ORDER BY Seconds), 1, 0) AS MidFlgHeatStart,
           IIF(ProductHeating < LAG(ProductHeating) OVER (ORDER BY Seconds), 1, 0) AS MidFlgHeatEnd
    FROM Dat2
),
Dat4 AS (
    SELECT ROW_NUMBER() OVER (ORDER BY Dat3.Seconds ASC) AS RunNo,
           MidFlgHeatStart, MidFlgHeatEnd, Seconds
    FROM Dat3
),
Dat5 AS (
    SELECT ROW_NUMBER() OVER (ORDER BY dt1.CycleTSec ASC) AS RNo,
           dt1.PowerUse,
           dt4.RunNo,
           dt1.Temperature,
           dt1.CycleTSec
    FROM Dat4 dt4
    INNER JOIN Dat1 dt1 ON dt4.Seconds = dt1.Seconds
    WHERE dt4.MidFlgHeatStart = 1 OR dt4.MidFlgHeatEnd = 1
),
dat6 AS (
    SELECT TOP 1 
        (LEAD(CAST(Temperature AS FLOAT),1) OVER (ORDER BY CycleTSec) - CAST(Temperature AS FLOAT)) /
        (LEAD(CAST(CycleTSec AS FLOAT),1) OVER (ORDER BY CycleTSec) - CAST(CycleTSec AS FLOAT)) AS HeatUp
    FROM Dat5
    WHERE RNo IN (1,2)
),
dat7 AS (
    SELECT TOP 1 
        (LEAD(CAST(Temperature AS FLOAT),1) OVER (ORDER BY CycleTSec) - CAST(Temperature AS FLOAT)) /
        (LEAD(CAST(CycleTSec AS FLOAT),1) OVER (ORDER BY CycleTSec) - CAST(CycleTSec AS FLOAT)) AS HeatUp
    FROM Dat5
    WHERE RNo IN (3,4)
),
dat8 AS (
    SELECT IIF(
        (SELECT TOP 1 (LEAD(CAST(Temperature AS FLOAT),1) OVER (ORDER BY CycleTSec) - CAST(Temperature AS FLOAT)) /
         (LEAD(CAST(CycleTSec AS FLOAT),1) OVER (ORDER BY CycleTSec) - CAST(CycleTSec AS FLOAT))
         FROM Dat5 WHERE RNo IN (5,6)) IS NULL,
        0,
        (SELECT TOP 1 (LEAD(CAST(Temperature AS FLOAT),1) OVER (ORDER BY CycleTSec) - CAST(Temperature AS FLOAT)) /
         (LEAD(CAST(CycleTSec AS FLOAT),1) OVER (ORDER BY CycleTSec) - CAST(CycleTSec AS FLOAT))
         FROM Dat5 WHERE RNo IN (5,6))
    ) AS HeatUp
)
SELECT (ROUND(dat6.HeatUp,3) + ROUND(dat7.HeatUp,3) + ROUND(dat8.HeatUp,3)) /
       IIF(dat8.HeatUp = 0, 2, 3) AS HeatUpRate
FROM dat6, dat7, dat8;
";

            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;

            var result = await cmd.ExecuteScalarAsync();
            return result != null && result != DBNull.Value ? Convert.ToDouble(result) : 0;
        }

        public async Task<double> CalCycleTimeAsync(string? serial, string? auditId)
        {

            using var conn = await GetOpenConnectionAsync();

            string sql = string.IsNullOrEmpty(auditId)
                ? @"
            SELECT MAX(seconds) / 60.0
            FROM cfa_data_excel
            WHERE auditid = (
                SELECT TOP 1 AuditID
                FROM Audit
                WHERE Serial = @Serial
                ORDER BY AuditID DESC
            )"
                : @"
            SELECT MAX(seconds) / 60.0
            FROM cfa_data_excel
            WHERE auditid = @AuditId";

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;

            if (string.IsNullOrEmpty(auditId))
                cmd.Parameters.Add(new SqlParameter("@Serial", serial ?? (object)DBNull.Value));
            else
                cmd.Parameters.Add(new SqlParameter("@AuditId", auditId));

            var result = await cmd.ExecuteScalarAsync();
            return (result != null && result != DBNull.Value) ? Convert.ToDouble(result) : 0.0;
        }

public async Task<TemperatureResult> CalTemperatureTNAsync(
    string? serial,
    string? auditId,
    int[] gStartNoMain,
    int[] gEndNoMain,
    int mainFillTimes)
{
    var result = new TemperatureResult();

            using var conn = await GetOpenConnectionAsync();
          
    for (int i = 0; i < mainFillTimes; i++)
    {
        // ✅ Build dynamic SQL for each section
        string baseSql = @"
            WITH basedata AS (
                SELECT 
                    ROW_NUMBER() OVER (ORDER BY seconds ASC) AS SampleRun,
                    seconds, voltage, [current], [power], powerusage,
                    waterusage, temperature, waterpressure, watertemperature
                FROM cfa_data_excel
                WHERE auditid = " + (string.IsNullOrEmpty(auditId)
                    ? "(SELECT TOP 1 AuditID FROM Audit WHERE Serial = @Serial ORDER BY AuditID DESC)"
                    : "@AuditId") + @"
            ),
            TEMPMAX AS (
                SELECT 
                    CAST(waterusage AS FLOAT) * 
                    (
                        -0.00000001 * POWER(CAST(watertemperature AS FLOAT), 3)
                        + 0.000006 * POWER(CAST(watertemperature AS FLOAT), 2)
                        - 0.00002 * CAST(watertemperature AS FLOAT)
                        + 1
                    ) AS TempComp,
                    basedata.seconds,
                    basedata.temperature,
                    basedata.samplerun
                FROM basedata
                WHERE basedata.samplerun >= @StartNo AND basedata.samplerun <= @EndNo
            )
            SELECT MAX(TEMPMAX.temperature) FROM TEMPMAX;
        ";

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = baseSql;

        if (string.IsNullOrEmpty(auditId))
            cmd.Parameters.Add(new SqlParameter("@Serial", serial ?? (object)DBNull.Value));
        else
            cmd.Parameters.Add(new SqlParameter("@AuditId", auditId));

        cmd.Parameters.Add(new SqlParameter("@StartNo", gStartNoMain[i]));
        cmd.Parameters.Add(new SqlParameter("@EndNo", gEndNoMain.Length > i ? gEndNoMain[i] : 9999));

        var scalarResult = await cmd.ExecuteScalarAsync();
        result.TemperatureIn[i] = scalarResult != null && scalarResult != DBNull.Value
            ? Convert.ToDouble(scalarResult)
            : 0.0;
    }

    return result;
}
public async Task<double> CalEnergyAsync(string? serial, string? auditId)
{
    using var conn = await GetOpenConnectionAsync();

    // SQL with parameter placeholders only
    string sql = @"
SELECT MAX(CAST(PowerUsage AS FLOAT)) AS Energy
FROM cfa_data_excel
WHERE auditid = @AuditId";

    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;

    // Determine the AuditId parameter safely
    if (!string.IsNullOrWhiteSpace(auditId))
    {
        cmd.Parameters.Add(new SqlParameter("@AuditId", auditId));
    }
    else
    {
        // Use a subquery to get the latest AuditID by Serial
        cmd.CommandText = @"
SELECT MAX(CAST(PowerUsage AS FLOAT)) AS Energy
FROM cfa_data_excel
WHERE auditid = (SELECT TOP 1 AuditID FROM Audit WHERE Serial = @Serial ORDER BY AuditID DESC)";
        cmd.Parameters.Add(new SqlParameter("@Serial", serial ?? (object)DBNull.Value));
    }

    var result = await cmd.ExecuteScalarAsync();

    return (result != null && result != DBNull.Value) ? Convert.ToDouble(result) : 0.0;
}


public async Task<FinalRinseA> CalFinalRinseANAsync(
    string? serial,
    string? auditId,
    int[] gStartNoMain,
    int mainFillTimes)
{
    var result = new FinalRinseA(mainFillTimes);

    using var conn = await GetOpenConnectionAsync();

    for (int i = 0; i < mainFillTimes; i++)
    {
        // Determine sample range dynamically
        int startNo = gStartNoMain[i];
        int endNo = (i + 1 < gStartNoMain.Length) ? gStartNoMain[i + 1] : 9999;

        string sql = $@"
WITH basedata AS (
    SELECT ROW_NUMBER() OVER(ORDER BY seconds ASC) AS SampleRun,
           seconds, voltage, [current], [power],
           powerusage, waterusage, temperature,
           waterpressure, watertemperature
    FROM cfa_data_excel
    WHERE auditid = {(string.IsNullOrEmpty(auditId)
            ? $"(SELECT TOP 1 AuditID FROM Audit WHERE Serial = @Serial ORDER BY AuditID DESC)"
            : "@AuditId")}
),
AMPMAX AS (
    SELECT 
        CAST(waterusage AS FLOAT) * (
            -0.00000001 * POWER(CAST(watertemperature AS FLOAT), 3) +
             0.000006 * POWER(CAST(watertemperature AS FLOAT), 2) +
            -0.00002 * CAST(watertemperature AS FLOAT) + 1
        ) AS TempComp,
        basedata.seconds,
        basedata.[current],
        basedata.SampleRun
    FROM basedata
    WHERE basedata.SampleRun BETWEEN @StartNo AND @EndNo
)
SELECT MAX([current]) FROM AMPMAX;";

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;

        if (string.IsNullOrEmpty(auditId))
            cmd.Parameters.Add(new SqlParameter("@Serial", serial ?? (object)DBNull.Value));
        else
            cmd.Parameters.Add(new SqlParameter("@AuditId", auditId));

        cmd.Parameters.Add(new SqlParameter("@StartNo", startNo));
        cmd.Parameters.Add(new SqlParameter("@EndNo", endNo));

        var scalarResult = await cmd.ExecuteScalarAsync();
        result[i] = scalarResult != null && scalarResult != DBNull.Value
            ? Convert.ToDouble(scalarResult)
            : 0.0;
    }

    return result;
}
public async Task<double> CalVoltAsync(string? serial, string? auditId)
{
    using var conn = await GetOpenConnectionAsync();

    string sql = $@"
SELECT AVG(CAST(Voltage AS FLOAT))
FROM cfa_data_excel
WHERE auditid = {(string.IsNullOrEmpty(auditId)
        ? $"(SELECT TOP 1 AuditID FROM Audit WHERE Serial = @Serial ORDER BY AuditID DESC)"
        : "@AuditId")}";

    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;

    if (string.IsNullOrEmpty(auditId))
        cmd.Parameters.Add(new SqlParameter("@Serial", serial ?? (object)DBNull.Value));
    else
        cmd.Parameters.Add(new SqlParameter("@AuditId", auditId));

    var result = await cmd.ExecuteScalarAsync();
    return (result != null && result != DBNull.Value) ? Convert.ToDouble(result) : 0.0;
}

    }
}
