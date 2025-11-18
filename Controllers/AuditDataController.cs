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

        #region Full Calculation

        /// <summary>
        /// Run the full calculation sequence for a given serial and auditId.
        /// Returns all relevant calculation results including fill volumes, FVFR, temperatures, energy, amperage, and voltage.
        /// GET: api/CFACal/RunFullCalculation
        /// </summary>
        [HttpGet("RunFullCalculation")]
        public async Task<IActionResult> RunFullCalculation(string? serial, string? auditId)
        {
            try
            {
                // 1. Check Serial No
                string[] DataProduct = await _service.CheckSNNoByAuditIdAsync(auditId);

                // 2. Sample Runs
                var sampleRuns = await _service.InitSampleRunNoAsync(serial, auditId);
                if (!sampleRuns.Any())
                    return BadRequest(new { message = "No sample runs found." });

                // 3. Fill calculations
                var endSampleNos = sampleRuns.Select(sr => sr.EndSampleRun).ToList();
                var fillResult = await _service.CalTimedFinalFillsNAsync(serial, auditId, endSampleNos);
                double totalFillVolume = fillResult.FinalFills?.Sum() ?? 0;

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

                // 4. Perform calculations
                double fvfrValue = await _service.CalFVFRNAsync(serial, auditId, mainFillTimes, fillRanges);
                double incomingWaterTemp = await _service.CalInComWTempAsync(serial, auditId, mainFillTimes, fillRanges);
                double heatUpRate = await _service.CalHeatUpRateAsync(serial, auditId);
                double cycleTime = await _service.CalCycleTimeAsync(serial, auditId);

                var tempResult = await _service.CalTemperatureTNAsync(serial, auditId, mainFillStart.ToArray(), mainFillEnd.ToArray(), mainFillTimes);
                double mainWashTemp = tempResult.TemperatureIn[0];
                double finalRinseTemp = tempResult.TemperatureIn[mainFillTimes] == 0
                    ? tempResult.TemperatureIn[mainFillTimes - 1]
                    : tempResult.TemperatureIn[mainFillTimes];

                double energy = await _service.CalEnergyAsync(serial, auditId);

                var finalRinseResult = await _service.CalFinalRinseANAsync(serial, auditId, mainFillStart.ToArray(), mainFillTimes);
                double mainWashAmperage = finalRinseResult.Values[0];
                double finalRinseAmperage = finalRinseResult.Values[mainFillTimes - 1];

                var voltage = await _service.CalVoltAsync(serial, auditId);

                // 5. Get part limits
                var partLimits = await _service.GetPartLimitsAsync(DataProduct[1],"4625");

                // 6. Return combined results
                return Ok(new
                {
                    DataProduct = DataProduct[0],
                    SampleRuns = sampleRuns,
                    timedFills = fillResult.TimedFills,
                    FinalFills = fillResult.FinalFills,
                    MainFillIndicators = fillResult.MainFillIndicators,
                    FillVolume = totalFillVolume,
                    FVFR = fvfrValue,
                    IncomingWaterTemperature = incomingWaterTemp,
                    HeatUpRateCPerS = heatUpRate,
                    CycleTime = cycleTime,
                    MainWashTemp = mainWashTemp,
                    FinalRinseTemp = finalRinseTemp,
                    EnergyKWh = energy,
                    MainWashAmperage = mainWashAmperage,
                    FinalRinseAmperage = finalRinseAmperage,
                    Voltage = voltage,
                    PartLimits = partLimits   // <-- Added here
                });
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }


        #endregion


    }
}
