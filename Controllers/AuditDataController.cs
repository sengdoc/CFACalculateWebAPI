using CFACalculateWebAPI.Models;
using CFACalculateWebAPI.Services;
using Microsoft.AspNetCore.Mvc;

namespace CFACalculateWebAPI.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class CFACalController : ControllerBase
    {
        private readonly AuditDataService _service;

        public CFACalController(AuditDataService service)
        {
            _service = service;
        }

        /// <summary>
        /// Run full calculation for a given AuditId and TubType.
        /// </summary>
        /// <param name="auditId">The Audit ID (required).</param>
        /// <param name="TubType">
        /// Tub Type ("Top", "Bot", "Single").  
        /// Use "AUTO" to auto-detect from product data.
        /// </param>
        /// <returns>
        /// Calculation results including sample runs, timed fills, final fills, fill indicators,
        /// fill volume, FVFR, temperatures, energy, amperage, voltage, part limits, visual checks,
        /// additional fills, and Tub Type.
        /// </returns>
        /// <response code="200">Returns the calculation results.</response>
        /// <response code="400">If AuditId is missing, TubType invalid, or an error occurs.</response>
        [HttpGet("RunFullCalculation")]
        public async Task<IActionResult> RunFullCalculation(string? auditId, string? TubType)
        {
            try
            {
                if (string.IsNullOrEmpty(auditId))
                    return BadRequest(new { message = "AuditId is required" });

                // 1. Get Product Data
                string[] DataProduct = await _service.CheckSNNoByAuditIdAsync(auditId);

                if (string.IsNullOrEmpty(TubType) || TubType == "AUTO")
                {
                    if (string.IsNullOrEmpty(DataProduct[3]))
                    {
                        return BadRequest(new { message = "AUTO Tub Type not found. Please select a Tub Type." });
                    }

                    TubType = DataProduct[3].ToUpper() switch
                    {
                        "TOP" => "Top",
                        "BOT" => "Bot",
                        "SINGLE" => "Single",
                        _ => TubType
                    };
                }

                // 2. Sample Runs
                var sampleRuns = await _service.InitSampleRunNoAsync(DataProduct[2], auditId);
                if (!sampleRuns.Any())
                    return BadRequest(new { message = "No sample runs found." });

                // 3. Fill Calculations
                var endSampleNos = sampleRuns.Select(sr => sr.EndSampleRun).ToList();
                var fillResult = await _service.CalTimedFinalFillsNAsync(DataProduct[1], DataProduct[4], DataProduct[2], auditId, endSampleNos);
                double totalFillVolume = fillResult.FinalFills?.Sum() ?? 0;

                // 4. Main Fill Info
                var mainFillTimes = 0;
                var mainFillStart = new List<int>();
                var mainFillEnd = new List<int>();
                var fillRanges = new List<(int start, int end)>();

                for (int i = 0; i < fillResult.MainFillIndicators.Count; i++)
                {
                    if (fillResult.MainFillIndicators[i] > 0)
                    {
                        fillRanges.Add((sampleRuns[i].StartSampleRun, sampleRuns[i].EndSampleRun));
                        mainFillStart.Add(sampleRuns[i].StartSampleRun);
                        mainFillTimes++;
                    }
                }

                for (int i = 1; i < mainFillStart.Count; i++)
                {
                    mainFillEnd.Add(mainFillStart[i]);
                    if (i == mainFillStart.Count - 1) mainFillEnd.Add(9999);
                }

                // 5. Calculations
                double fvfrValue = await _service.CalFVFRNAsync(DataProduct[2], auditId, mainFillTimes, fillRanges);
                double incomingWaterTemp = await _service.CalInComWTempAsync(DataProduct[2], auditId, mainFillTimes, fillRanges);
                double heatUpRate = await _service.CalHeatUpRateAsync(DataProduct[2], auditId);
                double cycleTime = await _service.CalCycleTimeAsync(DataProduct[2], auditId);

                var tempResult = await _service.CalTemperatureTNAsync(DataProduct[2], auditId, mainFillStart.ToArray(), mainFillEnd.ToArray(), mainFillTimes);
                double mainWashTemp = tempResult.TemperatureIn[0];
                double finalRinseTemp = tempResult.TemperatureIn[mainFillTimes] == 0
                    ? tempResult.TemperatureIn[mainFillTimes - 1]
                    : tempResult.TemperatureIn[mainFillTimes];

                double energy = await _service.CalEnergyAsync(DataProduct[2], auditId);

                var finalRinseResult = await _service.CalFinalRinseANAsync(DataProduct[2], auditId, mainFillStart.ToArray(), mainFillTimes);
                double mainWashAmperage = finalRinseResult.Values[0];
                double finalRinseAmperage = finalRinseResult.Values[mainFillTimes - 1];

                var voltage = await _service.CalVoltAsync(DataProduct[2], auditId);

                // 6. Get Part Limits
                var partLimits = await _service.GetPartLimitsAsync(DataProduct[1], DataProduct[2], TubType ?? "","4625");

                // 7. Visual Checks
                var visualChecks = await _service.GetVisualChecksAsync(DataProduct[1], "4625");

                // 8. Return results
                return Ok(new
                {
                    vDataProduct = DataProduct[0],
                    vSampleRuns = sampleRuns,
                    vTimedFills = fillResult.TimedFills,
                    vFinalFills = fillResult.FinalFills,
                    vFillIndicators = fillResult.FillIndicators,
                    vFillVolume = totalFillVolume,
                    vFVFR = fvfrValue,
                    vIncomingWaterTemperature = incomingWaterTemp,
                    vHeatUpRateCPerS = heatUpRate,
                    vCycleTime = cycleTime,
                    vMainWashTemp = mainWashTemp,
                    vFinalRinseTemp = finalRinseTemp,
                    vEnergyKWh = energy,
                    vMainWashAmperage = mainWashAmperage,
                    vFinalRinseAmperage = finalRinseAmperage,
                    vVoltage = voltage,
                    vPartLimits = partLimits,
                    vVisualChecks = visualChecks,
                    vAdditionalFills = fillResult.AdditionalFills,
                    vTobType = TubType
                });
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = "An error occurred while processing the request.", detail = ex.Message });
            }
        }

        [HttpGet("GETBOMTest")]
        public async Task<IActionResult> GETBOMTest(string? CA, string? SerialNo, string? Task)
        {
            try
            {
                if (string.IsNullOrEmpty(CA))
                    return BadRequest(new { message = "CA is required" });

                if (string.IsNullOrEmpty(SerialNo))
                    return BadRequest(new { message = "SerialNo is required" });

                if (string.IsNullOrEmpty(Task))
                    return BadRequest(new { message = "Task is required" });

                // Get Visual Check only (BOM-based)
                var visualChecks = await _service.GetVisualChecksByCAAsync(CA, Task);

                return Ok(new
                {
                    vVisualChecks = visualChecks,
                    PartProduct = $"{CA}{SerialNo}" // helpful for frontend save
                });
            }
            catch (Exception ex)
            {
                return BadRequest(new
                {
                    message = "An error occurred while processing the request.",
                    detail = ex.Message
                });
            }
        }

        /// <summary>
        /// Save a test result for a product part.
        /// </summary>
        /// <param name="result">The test result data transfer object.</param>
        /// <returns>
        /// Success message if saved; otherwise, error details.
        /// </returns>
        /// <response code="200">Test result saved successfully.</response>
        /// <response code="400">If required fields are missing or saving fails.</response>
        [HttpPost("SaveResultTest")]
        public async Task<IActionResult> SaveResultTest([FromBody] SaveTestResultDTO result)
        {
            try
            {
                string partCA = "";
                string SerialNo = "";
                string TaskNo = "4625";
                if (result.TypeInput == "VSCHK")
                {
                    partCA = result.CA ?? "";
                    SerialNo = result.SerialNo ?? "";
                    TaskNo = result.Task ?? "";
                }
                else
                { 
                if (string.IsNullOrEmpty(result.PartProduct))
                {
                    return BadRequest(new { message = "PartProduct cannot be null or empty." });
                }

                if (result.PartProduct.Length < 15)
                {
                    return BadRequest(new { message = "PartProduct is too short to extract both parts." });
                }

                 partCA = result.PartProduct.Substring(0, 5);
                 SerialNo = result.PartProduct.Substring(6, 9);
                }
                string runNo = await _service.GetRunNumberAsync(SerialNo, TaskNo);
                bool isOK = await _service.SaveTestResultAsync(partCA, SerialNo, runNo, result, TaskNo);

                if (isOK)
                {
                    await _service.SaveTaskResultAsync(partCA, SerialNo, runNo, TaskNo);
                }
                else
                {
                    return BadRequest(new { message = "Failed to save the test result." });
                }

                return Ok(new { message = "Test result saved successfully.", partCA, SerialNo });
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = "An error occurred while saving the result.", detail = ex.Message });
            }
        }
    }
}
