using CFACalculateWebAPI.Data;
using CFACalculateWebAPI.Models;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;
using System;
using System.Data;
using System.Data.Common; // ✅ add this
using System.Data.SqlClient;
using System.Security.Cryptography;
using System.Text;

namespace CFACalculateWebAPI.Services
{
    public class AuditDataService
    {
        public class TimedFinalFillResult
        {
            public List<double> TimedFills { get; set; } = new List<double>();
            public List<double> FinalFills { get; set; } = new List<double>();
            public List<int> MainFillIndicators { get; set; } = new List<int>();
            public List<int> FillIndicators { get; set; } = new List<int>();
            public int AdditionalFills { get; set; }
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
                resultList.Add(reader.GetString(0) + " " + reader.GetString(1) + " " + reader.GetString(2));
                resultList.Add(reader.GetString(0));
                resultList.Add(reader.GetString(1));
                resultList.Add(reader.GetString(3));
                resultList.Add(reader.GetString(2));
            }
            return resultList.ToArray();
        }
        // Calculate Timed Final Fills
        public async Task<TimedFinalFillResult> CalTimedFinalFillsNAsync(string? partCA,string? partCADes, string? serial, string? auditId, List<int> endSampleNos)
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

            // ===== Calculate Timed Final FillResult =====
            
            var finalFillResult =  CalculateTimedFinalFillsAsync(partCA, partCADes, timedFills);
            return finalFillResult;
        }

        // Calculate Final Fills
        private  TimedFinalFillResult CalculateTimedFinalFillsAsync(string? partCA,string? partCADes, List<double> timedFills)
        {
            //Testing FLUSH -----------
            //            List<double> timedFillsForce = new List<double>
            //{
            //    2.168485394,
            //    0.304273066,
            //    0.417022391,
            //    0.424057904,
            //    0.427102543,
            //    0.421258652,
            //    1.94988262,
            //    0.497105106,
            //    1.962536001,
            //    0.299667137
            //};
            //            timedFills = timedFillsForce;
            //            partCA = "81627";
            //            partCADes = "DD60DCHX9 FP TW";
            //------------------

            //Testing FLUSH -----------
//            List<double> timedFillsForce = new List<double>
//{
//    1.591855198,
//    0.674322625,
//    0.43597485,
//    0.438507232,
//    0.439409727,
//    0.43517365,
//    1.494379927,
//    0.570845662,
//    1.493059314,
//    0.469014047
//};

//            timedFills = timedFillsForce;
//            partCA = "82163";
//            partCADes = "DD24DTX6HI1 US";
            //------------------
            int n = timedFills.Count;
            int NoOfFills = n;
            // Initialize result lists
            var RetTimedFills = new List<double>(new double[n]);
            var RetFinalFills = new List<double>(new double[n]);
            var gRunNoMainF = new List<int>(new int[n]);
            var gRunNoTopupF = new List<int>(new int[n]);
            var gRunNoFlushF = new List<int>(new int[n]);

            var gRunNoAll = new List<int>(new int[n]);


            




            // Threshold values for classification
            //   var rst =  FillInitialTargetsData.Data.TryGetValue(partCA, out var DataResult);

            if (string.IsNullOrWhiteSpace(partCA))
            {
                throw new ArgumentException("Part CA cannot be null or empty.");
            }

            var rst = FillInitialTargetsData.Data.TryGetValue(partCA, out var DataResult);

            // Remove "DISHDRAWER" if exists (case-insensitive)
            partCADes = partCADes?.Replace("DISHDRAWER", "", StringComparison.OrdinalIgnoreCase).Trim();

            if (rst == false)
            {
                throw new ArgumentException("Part CA not found in FillInitialTargetsData.");
            }
            double? thresholdAI = 0;
            double? thresholdAN = 0;
            double? thresholdAS = 0;
            double? thresholdAX = 0;
            double? thresholdBC = 0;
            if (DataResult != null)
            {
                thresholdAI = DataResult.Fill1;
                thresholdAN = DataResult.Fill2;
                thresholdAS = DataResult.Fill3;
                thresholdAX = DataResult.Fill4;
                thresholdBC = DataResult.Fill5;
            }
            int mainFillCount = 0;
            int flushHistoryCount = 0;
          

            for (int index = 0; index < timedFills.Count; index++)
            {
                double value = timedFills[index];
                string classification = CheckFillType(
                    fillValue: value,
                    findex: index,
                    allValues: timedFills,
                    thresholdAI, thresholdAN, thresholdAS, thresholdAX, thresholdBC,
                    ref mainFillCount,
                    ref flushHistoryCount,
                    partCADes ?? "" //" DD60DCHX9 FP TW" // Example value containing H
                );

            
                //select* From part where part = '81627'

                Console.WriteLine($"Check {value} → {classification}");
                if (classification == "Main Fill")
                {

                    gRunNoMainF[index] = 1;  // Mark as "Main Fill"
                    gRunNoAll[index] = 1;

                }
                else if (classification == "Topup")
                {
                    gRunNoTopupF[index] = 2;  // Mark as "Topup"    
                    gRunNoAll[index] = 2;
                }
                else if (classification == "Flush")
                {
                    gRunNoFlushF[index] = 3;  // Mark as "Flush"
                    gRunNoAll[index] = 3;

                }
            }

            for (int x = 0; x < n; x++)
            {

                if (gRunNoAll[x] == 1)
                {
                    RetTimedFills[x] = timedFills[x];
                }

            }
            RetTimedFills = RetTimedFills.Where(x => x != 0).ToList();
            //=============
            int j = 0;
            bool isFinalFill = false;
            for (int y = 0; y < n; y++)
            {
                if (gRunNoAll[y] == 1) // Is Main Fill
                {
                    isFinalFill = false;
                    RetFinalFills[j] = timedFills[y];
                    j++;
                }
                else if (gRunNoTopupF[y] == 2 && isFinalFill == false)  // Is Firt Top up fill.
                {
                    RetFinalFills[j - 1] += timedFills[y];
                    isFinalFill = true;

                }
                else if (gRunNoTopupF[y] == 2 && isFinalFill == true)  // Is Top up fill again.
                {
                    RetFinalFills[j - 1] += timedFills[y];
                    isFinalFill = true;

                }
            }
            RetFinalFills = RetFinalFills.Where(x => x != 0).ToList();



            bool HaveFlush = gRunNoAll.Any(x => x == 3);
            var rst2 = NoOfFillsData.Data.TryGetValue(partCA, out var NoOfFillBOM);
            //Logic check Additional Fills
            int AdditionalFills = 0;
            AdditionalFills = NoOfFills - ((int)NoOfFillBOM);
            if (HaveFlush)
            { AdditionalFills = AdditionalFills - 4; }

            // Return the result as an object of TimedFinalFillResult
            return new TimedFinalFillResult
            {
                TimedFills = RetTimedFills,
                FinalFills = RetFinalFills,
                MainFillIndicators = gRunNoMainF,
                FillIndicators = gRunNoAll,
                AdditionalFills = AdditionalFills
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



        public async Task<List<PartLimit>> GetPartLimitsAsync(string parentPart, string serialNo, string typeTub, string taskNo)
        {
            var limits = new List<PartLimit>();

            using var conn = await GetOpenConnectionAsync();

            var sql = new StringBuilder(@"
WITH LatestRun AS (
    SELECT tsl.*
    FROM test_result_lis tsl
    INNER JOIN (
        SELECT test_part, serial, test_info1, test_unit_id,
               MAX(run_number) AS max_run
        FROM test_result_lis
        WHERE test_part = '595130'
          AND serial = @Serial
        GROUP BY test_part, serial, test_info1, test_unit_id
    ) lm ON tsl.test_part = lm.test_part
         AND tsl.serial = lm.serial
         AND tsl.test_info1 = lm.test_info1
         AND tsl.test_unit_id = lm.test_unit_id
         AND tsl.run_number = lm.max_run
)
SELECT
    p.part,
    p.class,
    p.description,
    ps.task_reference,
    CASE 
        WHEN p.class = 'TS_CFA_FVFR' THEN tsl.test_value - 2
        WHEN p.class NOT IN ('TS_CFA_ENER') THEN CAST(pt.lower_limit_value AS DECIMAL(18,2)) / 1000
        ELSE pt.lower_limit_value
    END AS lower_limit_k,
    CASE 
        WHEN p.class = 'TS_CFA_FVFR' THEN tsl.test_value + 2
        WHEN p.class NOT IN ('TS_CFA_ENER') THEN CAST(pt.upper_limit_value AS DECIMAL(18,2)) / 1000
        ELSE pt.upper_limit_value
    END AS upper_limit_k
FROM part_structure ps
INNER JOIN part p ON ps.component = p.part
INNER JOIN part_issue pii ON pii.part = p.part
INNER JOIN part_structure ps2 ON ps2.component = ps.part
INNER JOIN part_test pt ON ps.component = pt.part AND pt.part_issue = pii.part_issue
INNER JOIN LatestRun tsl ON tsl.test_part = '595130'
WHERE ps2.part = @ParentPart
");

            if (typeTub == "Top" || typeTub == "Bot")
            {
                sql.AppendLine("  AND tsl.test_info1 = @TypeTub");
            }

            sql.AppendLine(@"
  AND tsl.test_unit_id = 'Celsius'
  AND ps2.task = @TaskNo
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
      'TS_CFA_HEATUP', 'TS_CFA_VOLT', 'TS_CFA_MWA','TS_CFA_CYCLET','TS_CFA_ADF'
  )
ORDER BY ps.task_reference;
");

            using var cmd = new SqlCommand(sql.ToString(), (SqlConnection)conn);

            cmd.Parameters.AddWithValue("@ParentPart", parentPart);
            cmd.Parameters.AddWithValue("@Serial", serialNo);
            cmd.Parameters.AddWithValue("@TaskNo", taskNo); // <-- dynamic task

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

        // DD CFA ONLY
        public async Task<List<VisualCheckItem>> GetVisualChecksAsync(string parentPart, string task = "4625")
        {
            var visualChecks = new List<VisualCheckItem>();

            using var conn = await GetOpenConnectionAsync();

            var sql = @"
    SELECT DISTINCT
        p.part,
        p.description,
        p.class,
        pt.lower_limit_value,
        pt.upper_limit_value,
        ps2.task_reference   -- add this
    FROM part_structure ps
    INNER JOIN part_structure ps2 
        ON ps2.part = ps.component AND ps.task = ps2.task
    INNER JOIN part p 
        ON ps2.component = p.part
    INNER JOIN part_test pt 
        ON p.part = pt.part
    WHERE ps.part=@ParentPart 
      AND ps.task=@Task
      AND ps.eff_start <= GETDATE() 
      AND ps.eff_close >= GETDATE()
      AND ps2.eff_start <= GETDATE() 
      AND ps2.eff_close >= GETDATE()
      AND p.class NOT IN (
          'TS_CFA_INWT','TS_CFA_FVFR','TS_CFA_FVOL',
          'TS_CFA_FT1','TS_CFA_FT2','TS_CFA_FT3','TS_CFA_FT4','TS_CFA_FT5',
          'TS_CFA_FF1','TS_CFA_FF2','TS_CFA_FF3','TS_CFA_FF4','TS_CFA_FF5',
          'TS_CFA_MWT','TS_CFA_FNT','TS_CFA_ENER','TS_CFA_HEATUP','TS_CFA_VOLT',
          'TS_CFA_MWA','TS_CFA_CYCLET','TS_CFA_ADF'
      )
    ORDER BY p.class, ps2.task_reference;
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

        public async Task<List<VisualCheckItem>> GetVisualChecksByCAAsync(
      string? CA,
      string? task)
        {
            var visualChecks = new List<VisualCheckItem>();

            if (string.IsNullOrEmpty(CA) || string.IsNullOrEmpty(task))
                return visualChecks;

            using var conn = await GetOpenConnectionAsync();

            var sql = @"
        SELECT
            ps.component,
            p.description,
            p.class,
            pt.lower_limit_value,
            pt.upper_limit_value,
            pt.test_tag
        FROM part_structure ps
        INNER JOIN part p ON p.part = ps.component
        INNER JOIN part_test pt ON pt.part = p.part
        INNER JOIN part_issue pi ON pi.part = p.part
        WHERE ps.part = @CA
          AND ps.task = @Task
          AND ps.eff_start <= GETDATE()
          AND ps.eff_close >= GETDATE()
          AND pi.eff_start <= GETDATE()
          AND pi.eff_close >= GETDATE()
        ORDER BY ps.task_reference;
    ";

            using var cmd = new SqlCommand(sql, (SqlConnection)conn);
            cmd.Parameters.AddWithValue("@CA", CA);
            cmd.Parameters.AddWithValue("@Task", task);

            using var reader = await cmd.ExecuteReaderAsync();

            while (await reader.ReadAsync())
            {
                string? className = reader.IsDBNull(2) ? null : reader.GetString(2);
                double? lower = reader.IsDBNull(3) ? (double?)null : Convert.ToDouble(reader.GetValue(3));
                double? upper = reader.IsDBNull(4) ? (double?)null : Convert.ToDouble(reader.GetValue(4));
                string? testTag = reader.IsDBNull(5) ? null : reader.GetString(5);

                visualChecks.Add(new VisualCheckItem
                {
                    Part = reader.IsDBNull(0) ? null : reader.GetString(0),
                    Description = reader.IsDBNull(1) ? null : reader.GetString(1),
                    Class = className,
                    lowerLimit = lower?.ToString(),
                    upperLimit = upper?.ToString(),
                    test_tag = testTag
                });
            }

            return visualChecks;
        }



        public async Task<bool> SaveTestResultAsync(string partCa, string SerialNo, string runNo, SaveTestResultDTO result, string taskNo)
        {
            bool isSuccess = false;

            // Remove leading placeholder item if present (first element null or all fields empty)
            if (result?.vVisualResults != null && result.vVisualResults.Count > 0)
            {
                var first = result.vVisualResults[0];
                bool isEmptyFirst = first == null ||
                    (string.IsNullOrWhiteSpace(first.PartNo) &&
                     string.IsNullOrWhiteSpace(first.ResultValue) &&
                     string.IsNullOrWhiteSpace(first.tstStatus) &&
                     string.IsNullOrWhiteSpace(first.Comment));

                if (isEmptyFirst)
                    result.vVisualResults.RemoveAt(0);
            }

            if (result?.vAutoResults != null && result.vAutoResults.Count > 0)
            {
                var firstAuto = result.vAutoResults[0];
                bool isEmptyFirstAuto = firstAuto == null ||
                    (string.IsNullOrWhiteSpace(firstAuto.PartNo) &&
                     string.IsNullOrWhiteSpace(firstAuto.ResultValue) &&
                     string.IsNullOrWhiteSpace(firstAuto.tstStatus) &&
                     string.IsNullOrWhiteSpace(firstAuto.Comment));

                if (isEmptyFirstAuto)
                    result.vAutoResults.RemoveAt(0);
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
                        AddTestResultParameters(cmd, partCa, SerialNo, taskNo, "000", runNo, testPart, testResult, testStatus, "1");
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
                        AddTestResultParameters(cmd, partCa, SerialNo, taskNo, "000", runNo, testPart, testResult, testStatus, "1");
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


        // ---------------------------- FLUSH LOGIC ----------------------------
        public static bool IsFlush(List<double> allValues, double xValueCutLast, int flushHistoryCount, string resultB6)
        {
            bool containsH = !string.IsNullOrEmpty(resultB6) && resultB6.Contains("H");

            double sum = 0;
            for (int i = 0; i < allValues.Count - 1; i++)
                sum += allValues[i];

            bool conditionA = containsH &&
                              sum > 1.2 && sum < 2.0 &&
                              xValueCutLast > 0.5 &&
                              allValues.Count - 1 == 4;

            bool conditionB = flushHistoryCount >= 1 && flushHistoryCount < 4;

            return conditionA || conditionB;
        }

        // ------------------ Check Fill Type ------------------
        public static string CheckFillType(double fillValue,
                                           int findex,
                                           List<double> allValues,
                                           double? thresholdAI,
                                           double? thresholdAN,
                                           double? thresholdAS,
                                           double? thresholdAX,
                                           double? thresholdBC,
                                           ref int mainFillCount,
                                           ref int flushHistoryCount,
                                           string DescCA)
        {
            // Build allValuesCut for flush check (last 5 values)
            var allValuesCut = new List<double>();
            bool startAdding = false;
            int count = 0;
            double xValueCutLast = 0;

            for (int i = 0; i < allValues.Count; i++)
            {
                if (i == findex) startAdding = true;
                if (startAdding)
                {
                    allValuesCut.Add(allValues[i]);
                    count++;
                }
                if (count == 5)
                {
                    xValueCutLast = allValues[i];
                    break;
                }
            }

            if (IsFlush(allValuesCut, xValueCutLast, flushHistoryCount, DescCA))
            {
                flushHistoryCount++;
                return "Flush";
            }

            // Determine Main Fill / Topup
            string fillType = "Topup";

            double? threshold = mainFillCount switch
            {
                0 => thresholdAI,
                1 => thresholdAN,
                2 => thresholdAS,
                3 => thresholdAX,
                4 => thresholdBC,
                _ => null
            };

            if (threshold.HasValue && threshold.Value != 0 &&
                fillValue >= threshold.Value - 0.2 &&
                fillValue <= threshold.Value + 0.2)
            {
                mainFillCount++;
                fillType = "Main Fill";
            }

            return fillType;
        }

        /// <summary>
        /// Get Description for a specific part number from database (async)
        /// </summary>
        public async Task<string?> GetDescriptionByPartAsync(string partNumber)
        {
            if (string.IsNullOrWhiteSpace(partNumber))
                throw new ArgumentException("Part number cannot be null or empty.", nameof(partNumber));

            string? description = null;
            string sql = "SELECT description FROM part WHERE part = @part";

            // Use async disposal to safely close the connection
            await using var conn = await GetOpenConnectionAsync();
            await using var cmd = new SqlCommand(sql, (SqlConnection)conn);

            cmd.Parameters.Add("@part", SqlDbType.VarChar).Value = partNumber;

            // Execute query asynchronously
            object? result = await cmd.ExecuteScalarAsync();
            if (result != null && result != DBNull.Value)
            {
                description = result.ToString();

                // Remove "DISHDRAWER" if exists (case-insensitive)
                description = description?.Replace("DISHDRAWER", "", StringComparison.OrdinalIgnoreCase).Trim();
            }

            return description;
        }




    }
}
