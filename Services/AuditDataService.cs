using CFACalculateWebAPI.Data;
using CFACalculateWebAPI.Models;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using System.Data.Common; // ✅ add this
using System.Data.SqlClient;
using System.Text;
using Newtonsoft.Json;
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
        // Check S/N by AuditID  
        public async Task<string[]> CheckSNNoByAuditIdAsync(string? auditId)
        {
            var resultList = new List<string>();

            // Build SQL like Delphi
            var sql = @"SELECT TOP 1 p.part,ad.Serial,p.description,ad.Comments FROM Audit ad
                      inner join serial_track st on st.serial = ad.Serial 
                      inner join part p on p.part = st.part 
                      WHERE AuditID = @AuditID";
            using var conn = await GetOpenConnectionAsync();


            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;

            cmd.Parameters.Add(new SqlParameter("@AuditID", auditId!));

            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                resultList.Add(reader.GetString(0) + " " + reader.GetString(1)+" "+ reader.GetString(2));
                resultList.Add(reader.GetString(0));
                resultList.Add(reader.GetString(1));
                resultList.Add(reader.GetString(3));
            }
            return resultList.ToArray();
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



public async Task<List<PartLimit>> GetPartLimitsAsync(string parentPart, string serialNo, string typeTub)
    {
        var limits = new List<PartLimit>();

        using var conn = await GetOpenConnectionAsync();

        var sql = new StringBuilder(@"
SELECT DISTINCT
    p.part,
    p.class,
    p.description,
    ps.task_reference,
    IIF(
        p.class = 'TS_CFA_FVFR',
        tsl.test_value - 2,
        IIF(
            p.class NOT IN ('TS_CFA_ENER'),
            CAST(pt.lower_limit_value AS DECIMAL(18,2)) / 1000,
            pt.lower_limit_value
        )
    ) AS lower_limit_k,
    IIF(
        p.class = 'TS_CFA_FVFR',
        tsl.test_value + 2,
        IIF(
            p.class NOT IN ('TS_CFA_ENER'),
            CAST(pt.upper_limit_value AS DECIMAL(18,2)) / 1000,
            pt.upper_limit_value
        )
    ) AS upper_limit_k
FROM part_structure ps
INNER JOIN part p ON ps.component = p.part
INNER JOIN part_issue pii ON pii.part = p.part
INNER JOIN part_structure ps2 ON ps2.component = ps.part
INNER JOIN part_test pt ON ps.component = pt.part AND pt.part_issue = pii.part_issue
INNER JOIN test_result_lis tsl ON tsl.test_part = '595130'
WHERE ps2.part = @ParentPart
  AND tsl.serial = @Serial
");

        // Optional filter
        if (typeTub == "Top" || typeTub == "Bot")
        {
            sql.AppendLine("  AND tsl.test_info1 = @TypeTub");
        } 
        //else = SINGLE

        sql.AppendLine(@"
  AND tsl.test_unit_id = 'Celsius'
  AND ps2.task = 4625
  AND ps.eff_start <= GETDATE()
  AND ps.eff_close >= GETDATE()
  AND pii.eff_start <= GETDATE()
  AND pii.eff_close >= GETDATE()
  AND ps2.eff_start <= GETDATE()
  AND ps2.eff_close >= GETDATE()
  AND p.class IN (
      'TS_CFA_INWT', 'TS_CFA_FVFR', 'TS_CFA_FVOL', 'TS_CFA_FT1',
      'TS_CFA_FT2', 'TS_CFA_FT3', 'TS_CFA_FT4', 'TS_CFA_FT5',
      'TS_CFA_FF1', 'TS_CFA_FF2', 'TS_CFA_FF3', 'TS_CFA_FF4',
      'TS_CFA_FF5', 'TS_CFA_MWT', 'TS_CFA_FNT', 'TS_CFA_ENER',
      'TS_CFA_HEATUP', 'TS_CFA_VOLT', 'TS_CFA_MWA','TS_CFA_CYCLET'
  )
ORDER BY ps.task_reference;
");

        // Use SqlCommand (NOW AddWithValue works)
        using var cmd = new SqlCommand(sql.ToString(), (SqlConnection)conn);

        // Always add mandatory parameters
        cmd.Parameters.AddWithValue("@ParentPart", parentPart);
        cmd.Parameters.AddWithValue("@Serial", serialNo);

        // Optional parameter — only add when needed
        if (typeTub == "Top" || typeTub == "Bot")
        {
            cmd.Parameters.AddWithValue("@TypeTub", typeTub);
        }

        using var reader = await cmd.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
                limits.Add(new PartLimit
                {
                    Part = reader.IsDBNull(0) ? null : reader.GetString(0),
                    Class = reader.IsDBNull(1) ? null : reader.GetString(1),
                    Description = reader.IsDBNull(2) ? null : reader.GetString(2),
                    TaskReference = reader.IsDBNull(3) ? null : reader.GetString(3),
                    LowerLimit = reader.IsDBNull(4) ? (double?)null : Convert.ToDouble(reader.GetValue(4)),
                    UpperLimit = reader.IsDBNull(5) ? (double?)null : Convert.ToDouble(reader.GetValue(5)),
                });

            }

            return limits;
    }

        public async Task<List<VisualCheckItem>> GetVisualChecksAsync(string parentPart, string task = "4625")
        {
            var visualChecks = new List<VisualCheckItem>();

            using var conn = await GetOpenConnectionAsync();

            var sql = @"
        SELECT
    p.part,
    p.description,
    p.class,
    pt.lower_limit_value,
    pt.upper_limit_value
FROM part_structure ps
INNER JOIN part_structure ps2 ON ps2.part = ps.component AND ps.task = ps2.task
INNER JOIN part p ON ps2.component = p.part
INNER JOIN part_test pt ON p.part = pt.part
WHERE ps.part=@ParentPart AND ps.task=@Task
AND ps.eff_start <= GETDATE() AND ps.eff_close >= GETDATE()
AND ps2.eff_start <= GETDATE() AND ps2.eff_close >= GETDATE()
AND p.class NOT IN (
    'TS_CFA_INWT','TS_CFA_FVFR','TS_CFA_FVOL',
    'TS_CFA_FT1','TS_CFA_FT2','TS_CFA_FT3','TS_CFA_FT4','TS_CFA_FT5',
    'TS_CFA_FF1','TS_CFA_FF2','TS_CFA_FF3','TS_CFA_FF4','TS_CFA_FF5',
    'TS_CFA_MWT','TS_CFA_FNT','TS_CFA_ENER','TS_CFA_HEATUP','TS_CFA_VOLT',
    'TS_CFA_MWA','TS_CFA_CYCLET'
)
ORDER BY p.class,ps2.task_reference;
    ";

            using var cmd = new SqlCommand(sql, (SqlConnection)conn);
            cmd.Parameters.AddWithValue("@ParentPart", parentPart);
            cmd.Parameters.AddWithValue("@Task", task);

            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var className = reader.IsDBNull(2) ? null : reader.GetString(2);

                // Get limits
                double? lower = reader.IsDBNull(3) ? (double?)null : Convert.ToDouble(reader.GetValue(3));
                double? upper = reader.IsDBNull(4) ? (double?)null : Convert.ToDouble(reader.GetValue(4));

                // Conditional divide by 1000
                if (className != null && (className == "TS_CFA_RINV" || className == "TS_CFA_SPYSPD" || className == "TS_CFA_RFS"))
                {
                    if (lower.HasValue) lower /= 1000;
                    if (upper.HasValue) upper /= 1000;
                }

                visualChecks.Add(new VisualCheckItem
                {
                    Part = reader.IsDBNull(0) ? null : reader.GetString(0),
                    Description = reader.IsDBNull(1) ? null : reader.GetString(1),
                    Class = className,
                    lowerLimit = lower?.ToString(),
                    upperLimit = upper?.ToString(),
                });
            }


            return visualChecks;
        }

        public async Task<string> GetRunNumberAsync(string SerialNo, string atask)
        {
            string run = "1";

            // SQL query to get the maximum run_number
            string queryString = @"
        SELECT MAX(run_number) AS run 
        FROM test_result 
        WHERE serial = @SerialNo AND task = @Task";

            // Open the connection asynchronously
            using var conn = await GetOpenConnectionAsync(); // Assuming you have this method to open the connection asynchronously
            using var cmd = new SqlCommand(queryString, (SqlConnection)conn);

            // Add parameters to the command to prevent SQL injection
            cmd.Parameters.AddWithValue("@SerialNo", SerialNo);
            cmd.Parameters.AddWithValue("@Task", atask);

            // Execute the query asynchronously and read the result
            using var reader = await cmd.ExecuteReaderAsync();

            if (await reader.ReadAsync())
            {
                // Check if the result is null or empty, and handle accordingly
                if (reader.IsDBNull(reader.GetOrdinal("run")))
                {
                    run = "1";
                }
                else
                {
                    // Increment the max value of the 'run' field
                    run = (reader.GetInt32(reader.GetOrdinal("run")) + 1).ToString();
                }
            }

            return run;
        }

        public async Task<bool> SaveTestResultAsync(string partCa, string SerialNo, string runNo, SaveTestResultDTO result)
        {
            bool isSuccess = false;

            // Check if vVisualResults and vAutoResults are not null and contain elements before removing the first item
            if (result.vVisualResults != null && result.vVisualResults.Any())
            {
                result.vVisualResults.RemoveAt(0); // Remove first item if not empty
            }

            if (result.vAutoResults != null && result.vAutoResults.Any())
            {
                result.vAutoResults.RemoveAt(0); // Remove first item if not empty
            }

            using var conn = await GetOpenConnectionAsync();

            // Start a transaction to ensure all operations are atomic
            using var transaction = await conn.BeginTransactionAsync();

            try
            {
                // Build the SQL Insert Statement for test_result
                var sql = @"
        INSERT INTO test_result 
            (part, serial, task, task_reference, run_number, test_part, date_tested, test_result, test_status, station)
        VALUES 
            (@Part, @Serial, @Task, @TaskReference, @RunNumber, @TestPart, GETDATE(), @TestResult, @TestStatus, @Station)";

                using var cmd = conn.CreateCommand();
                cmd.CommandText = sql;
                cmd.Transaction = transaction; // Associate the command with the transaction

                // Helper method to add parameters to the command
                void AddTestResultParameters(DbCommand command, string part, string serial, string task, string taskReference,
                                             string runNumber, string testPart, string testResult, string testStatus, string station)
                {
                    command.Parameters.Clear();
                    command.Parameters.Add(new SqlParameter("@Part", part ?? ""));
                    command.Parameters.Add(new SqlParameter("@Serial", serial ?? ""));
                    command.Parameters.Add(new SqlParameter("@Task", task ?? ""));
                    command.Parameters.Add(new SqlParameter("@TaskReference", taskReference ?? ""));
                    command.Parameters.Add(new SqlParameter("@RunNumber", runNumber ?? ""));
                    command.Parameters.Add(new SqlParameter("@TestPart", testPart ?? ""));
                    command.Parameters.Add(new SqlParameter("@TestResult", testResult ?? ""));
                    command.Parameters.Add(new SqlParameter("@TestStatus", testStatus ?? ""));
                    command.Parameters.Add(new SqlParameter("@Station", station ?? ""));
                }

                // Insert for vVisualResults
                if (result.vVisualResults != null && result.vVisualResults.Any())
                {
                    foreach (var visualResult in result.vVisualResults)
                    {
                        string testPart = visualResult.PartNo ?? "";
                        string testResult = visualResult.ResultValue ?? "";
                        string testStatus = visualResult.tstStatus ?? "";

                        // Set parameters and execute the insert command for each visual result
                        AddTestResultParameters(cmd, partCa, SerialNo, "4625", "000", runNo, testPart, testResult, testStatus, "1");
                        await cmd.ExecuteNonQueryAsync();
                    }
                }

                // Insert for vAutoResults
                if (result.vAutoResults != null && result.vAutoResults.Any())
                {
                    foreach (var autoResult in result.vAutoResults)
                    {
                        string testPart = autoResult.PartNo ?? "";
                        string testResult = autoResult.ResultValue ?? "";
                        string testStatus = autoResult.tstStatus ?? "";

                        // Set parameters and execute the insert command for each auto result
                        AddTestResultParameters(cmd, partCa, SerialNo, "4625", "000", runNo, testPart, testResult, testStatus, "1");
                        await cmd.ExecuteNonQueryAsync();
                    }
                }

                // Commit the transaction after all inserts are successful
                await transaction.CommitAsync();
                isSuccess = true;
            }
            catch (Exception ex)
            {
                // In case of error, roll back the transaction
                await transaction.RollbackAsync();
                // Log the exception for debugging purposes
                Console.WriteLine($"Error occurred: {ex.Message}");
            }

            return isSuccess;
        }


        public async Task SaveTaskResultAsync(string partCa, string SerialNo, string runNo, string task)
        {
            using var conn = await GetOpenConnectionAsync();

            // Step 1: Check the test_status of all part-tests in test_result where the task matches
            var checkStatusSql = @"
        SELECT DISTINCT test_status
        FROM test_result
        WHERE part = @Part 
            AND serial = @Serial 
            AND run_number = @RunNumber 
            AND task = @Task";

            using var checkCmd = conn.CreateCommand();
            checkCmd.CommandText = checkStatusSql;
            checkCmd.Parameters.Add(new SqlParameter("@Part", partCa ?? ""));
            checkCmd.Parameters.Add(new SqlParameter("@Serial", SerialNo ?? ""));
            checkCmd.Parameters.Add(new SqlParameter("@RunNumber", runNo ?? ""));
            checkCmd.Parameters.Add(new SqlParameter("@Task", task ?? ""));

            var testStatuses = new List<string>();
            using (var reader = await checkCmd.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                {
                    testStatuses.Add(reader.GetString(0));
                }
            }

            // Step 2: Determine task status ('P' or 'F')
            string taskStatus = testStatuses.Contains("F") ? "F" : "P";

            // Step 3: Insert or update task_result based on part, serial, run_number, and task
            var taskResultSql = @"
        IF EXISTS (SELECT 1 FROM task_result WHERE part = @Part AND serial = @Serial AND task = @Task)
        BEGIN
            UPDATE task_result
            SET task_status = @TaskStatus, date_tested = GETDATE(),run_number = @RunNumber
            WHERE part = @Part AND serial = @Serial AND task = @Task
        END
        ELSE
        BEGIN
            INSERT INTO task_result (part, serial, task, run_number, date_tested, task_status)
            VALUES (@Part, @Serial, @Task, @RunNumber, GETDATE(), @TaskStatus)
        END";

            using var taskCmd = conn.CreateCommand();
            taskCmd.CommandText = taskResultSql;
            taskCmd.Parameters.Add(new SqlParameter("@Part", partCa ?? ""));
            taskCmd.Parameters.Add(new SqlParameter("@Serial", SerialNo ?? ""));
            taskCmd.Parameters.Add(new SqlParameter("@RunNumber", runNo ?? ""));
            taskCmd.Parameters.Add(new SqlParameter("@Task", task ?? ""));
            taskCmd.Parameters.Add(new SqlParameter("@TaskStatus", taskStatus ?? ""));

            // Execute the command to insert or update the task_result
            await taskCmd.ExecuteNonQueryAsync();
        }




    }
}
