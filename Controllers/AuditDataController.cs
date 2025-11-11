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

        [HttpGet("InitSampleRun")]
        public async Task<IActionResult> InitSampleRun(string? serial, string? auditId)
        {
            try
            {
                var result = await _service.InitSampleRunNoAsync(serial, auditId);
                return Ok(result);
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        [HttpGet("CalTimedFills")]
        public async Task<IActionResult> CalTimedFills(string? serial, string? auditId, [FromQuery] int[] endSamples)
        {
            if (endSamples.Length == 0)
                return BadRequest(new { message = "endSamples cannot be empty" });

            try
            {
                var result = await _service.CalTimedFinalFillsNAsync(serial, auditId, endSamples.ToList());
                return Ok(result);
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        [HttpGet("CalFVFRN")]
        public async Task<IActionResult> CalFVFRN(
                    string? serial,
                    string? auditId,
                    int mainFillTimes = 4,
                    [FromQuery] int[] startNos = null!,
                    [FromQuery] int[] endNos = null!
                )
        {
            try
            {
                if (startNos == null || endNos == null || startNos.Length != endNos.Length)
                    return BadRequest(new { message = "startNos and endNos must have the same length." });

                // Build fill ranges
                var fillRanges = new List<(int start, int end)>();
                for (int i = 0; i < startNos.Length; i++)
                    fillRanges.Add((startNos[i], endNos[i]));

                // Call the calculation service
                double fvfrValue = await _service.CalFVFRNAsync(serial, auditId, mainFillTimes, fillRanges);

                return Ok(new
                {
                    Message = "FVFR calculation complete.",
                    FVFR = fvfrValue
                });
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        [HttpGet("CalInComWTemp")]
        public async Task<IActionResult> CalInComWTemp(
    string? serial,
    string? auditId,
    int mainFillTimes = 3,
    [FromQuery] int[] startNos = null!,
    [FromQuery] int[] endNos = null!)
        {
            try
            {
                if (startNos == null || endNos == null || startNos.Length != endNos.Length)
                    return BadRequest(new { message = "startNos and endNos must have the same length." });

                var fillRanges = new List<(int start, int end)>();
                for (int i = 0; i < startNos.Length; i++)
                    fillRanges.Add((startNos[i], endNos[i]));

                double avgTemp = await _service.CalInComWTempAsync(serial, auditId, mainFillTimes, fillRanges);

                return Ok(new
                {
                    Message = "Average water temperature calculation complete.",
                    AvgWaterTemp = avgTemp
                });
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        [HttpGet("CalHeatUpRate")]
        public async Task<IActionResult> CalHeatUpRate(string? serial, string? auditId)
        {
            try
            {
                // Call the service to calculate Heat-Up Rate
                double heatUpRate = await _service.CalHeatUpRateAsync(serial, auditId);

                return Ok(new
                {
                    Message = "Heat-Up Rate calculation complete.",
                    HeatUpRateCPerS = heatUpRate
                });
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }
        [HttpGet("CalCycleTime")]
        public async Task<IActionResult> CalCycleTime(string? serial, string? auditId)
        {
            try
            {
                double cycleTime = await _service.CalCycleTimeAsync(serial, auditId);
                return Ok(new
                {
                    Message = "Cycle Time calculated successfully.",
                    CycleTimeMinutes = cycleTime
                });
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        [HttpGet("CalTemperatureTN")]
        public async Task<IActionResult> CalTemperatureTN(
    string? serial,
    string? auditId,
    [FromQuery] int[] gStartNoMain,
    [FromQuery] int[] gEndNoMain,
    int mainFillTimes = 3)
        {
            try
            {
                var result = await _service.CalTemperatureTNAsync(serial, auditId, gStartNoMain, gEndNoMain, mainFillTimes);
                return Ok(new
                {
                    Message = "Temperature calculation successful.",
                    TemperatureIn = result.TemperatureIn
                });
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        // GET: api/Audit/Energy?serial=123&auditId=456
        [HttpGet("Energy")]
        public async Task<IActionResult> GetEnergy([FromQuery] string? serial, [FromQuery] string? auditId)
        {
            try
            {
                double energy = await _service.CalEnergyAsync(serial, auditId);
                return Ok(new { EnergyKWh = energy });
            }
            catch (Exception ex)
            {
                return BadRequest(new { Message = ex.Message });
            }
        }

        [HttpGet("FinalRinse")]
        public async Task<IActionResult> GetFinalRinse(
       string? serial,
       string? auditId,
       [FromQuery] int[] gStartNoMain,
       int mainFillTimes)
        {
            if (gStartNoMain == null || gStartNoMain.Length < mainFillTimes)
            {
                return BadRequest("Invalid start sample numbers provided.");
            }

            try
            {
                var finalRinseResult = await _service.CalFinalRinseANAsync(
                    serial,
                    auditId,
                    gStartNoMain,
                    mainFillTimes
                );

                // Return as JSON
                return Ok(new
                {
                    Values = finalRinseResult.Values
                });
            }
            catch (Exception ex)
            {
                // Log the exception (optional)
                return StatusCode(500, new { error = ex.Message });
            }
        }

        [HttpGet("Voltage")]
        public async Task<ActionResult<double>> GetVoltageAsync(
         [FromQuery] string? serial,
         [FromQuery] string? auditId)
        {
            try
            {
                var voltage = await _service.CalVoltAsync(serial, auditId);
                return Ok(voltage);
            }
            catch (Exception ex)
            {
                // Log error if needed
                return StatusCode(500, $"Error calculating voltage: {ex.Message}");
            }
        }

        [HttpGet("RunFullCalculation")]
        public async Task<IActionResult> RunFullCalculation(string? serial, string? auditId)
        {
            try
            {
                // Step 1: Get Sample Runs
                var sampleRuns = await _service.InitSampleRunNoAsync(serial, auditId);

                if (sampleRuns.Count == 0)
                    return BadRequest(new { message = "No sample runs found." });

                // Step 2: Build endSampleNos array
                var endSampleNos = sampleRuns.Select(sr => sr.EndSampleRun).ToList();

                // Step 3: Calculate timed & final fills
                var fillResult = await _service.CalTimedFinalFillsNAsync(serial, auditId, endSampleNos);

                // ✅ Step 4: Calculate total Final Fills (Fill Volume)
                double fillVolumeI = fillResult.FinalFills?.Sum() ?? 0;


                // mainFillTimes find by look in the fillResult.TimedFills if data in array > 0 count 1
                var mainFillTimes = 0;

                var TemperStrS = new List<int>();
                var TemperEndS = new List<int>();

                var fillRanges = new List<(int start, int end)>();
                for (int i = 0; i < fillResult.MainFillIndicators.Count; i++)
                {
                    if (fillResult.MainFillIndicators[i] > 0)
                    {
                        fillRanges.Add((sampleRuns[i].StartSampleRun, sampleRuns[i].EndSampleRun));

                        TemperStrS.Add(sampleRuns[i].StartSampleRun);
                        mainFillTimes++;
                    }
                }
                for (int i = 1; i < TemperStrS.Count; i++)
                {
                    TemperEndS.Add(TemperStrS[i]);
                    if ((i + 1) == TemperStrS.Count)
                        TemperEndS.Add(9999);
                }
                // Call the calculation service
                double fvfrValue = await _service.CalFVFRNAsync(serial, auditId, mainFillTimes, fillRanges);

                double IncomingWaterTemp = await _service.CalInComWTempAsync(serial, auditId, mainFillTimes, fillRanges);

                double heatUpRate = await _service.CalHeatUpRateAsync(serial, auditId);

                double cycleTime = await _service.CalCycleTimeAsync(serial, auditId);

                var TemperResult = await _service.CalTemperatureTNAsync(serial, auditId, TemperStrS.ToArray(), TemperEndS.ToArray(), mainFillTimes);

                double MainWashTempI = TemperResult.TemperatureIn[0];

                double FinalRinseTempI;
                if (TemperResult.TemperatureIn[mainFillTimes] == 0)
                    FinalRinseTempI = TemperResult.TemperatureIn[mainFillTimes - 1];
                else
                    FinalRinseTempI = TemperResult.TemperatureIn[mainFillTimes];

                double energy = await _service.CalEnergyAsync(serial, auditId);

                var finalRinseResult = await _service.CalFinalRinseANAsync(
             serial,
             auditId,
             TemperStrS.ToArray(),
             mainFillTimes
         );

                double MainWashAmperageI = finalRinseResult.Values[0];
                double FinalRinseAmperageI = finalRinseResult.Values[mainFillTimes - 1];

                var voltage = await _service.CalVoltAsync(serial, auditId);
                // Step 5: Return combined result
                return Ok(new
                {
                    SampleRuns = sampleRuns,
                    TimedFills = fillResult.TimedFills,
                    FinalFills = fillResult.FinalFills,
                    MainFillIndicators = fillResult.MainFillIndicators,
                    FillVolume = fillVolumeI,
                    FVFR = fvfrValue,
                    IncomingWaterTemperature = IncomingWaterTemp,
                    HeatUpRateCPerS = heatUpRate,
                    cycleTime = cycleTime,
                    MainWashTemp = MainWashTempI,
                    FinalRinseTemp = FinalRinseTempI,
                    EnergyKWh = energy,
                    MainWashAmperage = MainWashAmperageI,
                    FinalRinseAmperage = FinalRinseAmperageI,
                    Voltage = voltage

                });
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }






    }
}
