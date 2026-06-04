using System.IO;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using SRSS.IAM.Repositories.UnitOfWork;
using SRSS.IAM.Services.DTOs.SystematicReviewProject;
using SRSS.IAM.Repositories.Entities;

namespace SRSS.IAM.Services.ProjectExportService
{
    public class ProjectExportService : IProjectExportService
    {
        private readonly IUnitOfWork _unitOfWork;

        public ProjectExportService(IUnitOfWork unitOfWork)
        {
            _unitOfWork = unitOfWork;
        }

        public async Task<byte[]> ExportProjectsToExcelAsync(ProjectExportRequest request)
        {
            var projects = await _unitOfWork.SystematicReviewProjects.GetQueryable()
                .Where(p => !p.IsDeleted)
                .Include(p => p.ReviewProcesses)
                .Include(p => p.ProjectMembers)
                    .ThenInclude(pm => pm.User)
                .OrderByDescending(p => p.CreatedAt)
                .ToListAsync();

            using var workbook = new XLWorkbook();
            var worksheet = workbook.Worksheets.Add("Projects");

            // Header row
            var headers = new[]
            {
                "Code",
                "Title",
                "Domain",
                "Description",
                "Leader",
                "Status",
                "Timeline",
                "Total Processes",
                "Processes Done"
            };

            for (int i = 0; i < headers.Length; i++)
            {
                var cell = worksheet.Cell(1, i + 1);
                cell.Value = headers[i];
                cell.Style.Font.Bold = true;
                cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#E0E0E0");
                cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            }

            // Data rows
            int rowCount = 2;
            foreach (var project in projects)
            {
                var leader = project.ProjectMembers.FirstOrDefault(m => m.Role == ProjectRole.Leader)?.User;
                var timeline = (project.StartDate.HasValue || project.EndDate.HasValue)
                    ? $"{(project.StartDate?.ToString("yyyy-MM-dd") ?? "N/A")} - {(project.EndDate?.ToString("yyyy-MM-dd") ?? "N/A")}"
                    : "N/A";

                worksheet.Cell(rowCount, 1).Value = project.Code;
                worksheet.Cell(rowCount, 2).Value = project.Title;
                worksheet.Cell(rowCount, 3).Value = project.Domain ?? "N/A";
                worksheet.Cell(rowCount, 4).Value = project.Description ?? "N/A";
                worksheet.Cell(rowCount, 5).Value = leader?.FullName ?? "N/A";
                worksheet.Cell(rowCount, 6).Value = project.Status.ToString();
                worksheet.Cell(rowCount, 7).Value = timeline;
                worksheet.Cell(rowCount, 8).Value = project.ReviewProcesses.Count;
                worksheet.Cell(rowCount, 9).Value = project.ReviewProcesses.Count(p => p.Status == ProcessStatus.Completed);
                rowCount++;
            }

            // Formatting
            worksheet.Columns().AdjustToContents();
            worksheet.SheetView.FreezeRows(1);

            using var stream = new MemoryStream();
            workbook.SaveAs(stream);
            return stream.ToArray();
        }
    }
}
