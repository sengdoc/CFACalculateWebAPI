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
        /// Run full calculation for a given AuditId and TubType
        /// GET: api/CFACal/RunFullCalculation
        /// </summary>
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
                var sampleRuns = await _service.InitSampleRunNoAsync(DataProduct[0], auditId);
                if (!sampleRuns.Any())
                    return BadRequest(new { message = "No sample runs found." });

                // 3. Fill Calculations
                var endSampleNos = sampleRuns.Select(sr => sr.EndSampleRun).ToList();
                var fillResult = await _service.CalTimedFinalFillsNAsync(DataProduct[0], auditId, endSampleNos);
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
                double fvfrValue = await _service.CalFVFRNAsync(DataProduct[0], auditId, mainFillTimes, fillRanges);
                double incomingWaterTemp = await _service.CalInComWTempAsync(DataProduct[0], auditId, mainFillTimes, fillRanges);
                double heatUpRate = await _service.CalHeatUpRateAsync(DataProduct[0], auditId);
                double cycleTime = await _service.CalCycleTimeAsync(DataProduct[0], auditId);

                var tempResult = await _service.CalTemperatureTNAsync(DataProduct[0], auditId, mainFillStart.ToArray(), mainFillEnd.ToArray(), mainFillTimes);
                double mainWashTemp = tempResult.TemperatureIn[0];
                double finalRinseTemp = tempResult.TemperatureIn[mainFillTimes] == 0
                    ? tempResult.TemperatureIn[mainFillTimes - 1]
                    : tempResult.TemperatureIn[mainFillTimes];

                double energy = await _service.CalEnergyAsync(DataProduct[0], auditId);

                var finalRinseResult = await _service.CalFinalRinseANAsync(DataProduct[0], auditId, mainFillStart.ToArray(), mainFillTimes);
                double mainWashAmperage = finalRinseResult.Values[0];
                double finalRinseAmperage = finalRinseResult.Values[mainFillTimes - 1];

                var voltage = await _service.CalVoltAsync(DataProduct[0], auditId);

                // 6. Get Part Limits
                var partLimits = await _service.GetPartLimitsAsync(DataProduct[1], DataProduct[2], TubType ?? "");

                // 7. Get Part Limits
                var visualChecks = await _service.GetVisualChecksAsync(DataProduct[1],"4625");

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
                    vVisualChecks = visualChecks
                });
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = "An error occurred while processing the request.", detail = ex.Message });
            }
        }


        [HttpPost("SaveResultTest")]
        public async Task<IActionResult> SaveResultTest([FromBody] SaveTestResultDTO result)
        {
            try
            {
                if (string.IsNullOrEmpty(result.PartProduct))
                {
                    return BadRequest(new { message = "PartProduct cannot be null or empty." });
                }

                // Ensure the string has enough length to safely extract the parts
                if (result.PartProduct.Length < 15)
                {
                    return BadRequest(new { message = "PartProduct is too short to extract both parts." });
                }

                // Extract the first part (82319)
                string partCA = result.PartProduct.Substring(0, 5); // Starting at index 0, take 5 characters

                // Extract the second part (EUV482568)
                string SerialNo = result.PartProduct.Substring(6, 9); // Starting at index 6, take 9 characters

                // Assuming your SaveTestResultAsync method works with result, you may want to modify it to include the extracted parts
                // If you want to store them or perform any action with part1 and part2, you can do so here.

                string runNo = await _service.GetRunNumberAsync(SerialNo, "4625");
                // Save the test result asynchronously
              bool isOK =  await _service.SaveTestResultAsync(partCA, SerialNo, runNo, result);
                if (isOK)
                {
                    await _service.SaveTaskResultAsync(partCA, SerialNo, runNo, "4625");
                }else
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
