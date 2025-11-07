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

                // Step 4: Return combined result
                return Ok(new
                {
                    SampleRuns = sampleRuns,
                    TimedFills = fillResult.TimedFills,
                    FinalFills = fillResult.FinalFills,
                    MainFillIndicators = fillResult.MainFillIndicators
                });
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }
    }
}
