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
                var fillRanges = new List<(int start, int end)>();
                for (int i = 0; i < fillResult.MainFillIndicators.Count; i++)
                {
                    if (fillResult.MainFillIndicators[i] > 0)
                    {
                        fillRanges.Add((sampleRuns[i].StartSampleRun , sampleRuns[i].EndSampleRun ));
                        mainFillTimes++;
                    }
                }
          

                 // Call the calculation service
                double fvfrValue = await _service.CalFVFRNAsync(serial, auditId, mainFillTimes, fillRanges);

                double IncomingWaterTemp = await _service.CalInComWTempAsync(serial, auditId, mainFillTimes, fillRanges);
                 
                // Step 5: Return combined result
                return Ok(new
                {
                    SampleRuns = sampleRuns,
                    TimedFills = fillResult.TimedFills,
                    FinalFills = fillResult.FinalFills,
                    MainFillIndicators = fillResult.MainFillIndicators,
                    FillVolume = fillVolumeI,
                    FVFR = fvfrValue,
                    IncomingWaterTemperature = IncomingWaterTemp

                });
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        


    }
}
