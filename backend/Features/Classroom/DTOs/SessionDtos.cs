namespace BackendServer.Features.Classroom.DTOs
{
    public class CreateSessionDto
    {
        public string PresentationTitle { get; set; } = string.Empty;
        public int SlideCount { get; set; }
        public Guid? CourseId { get; set; }
    }
}
