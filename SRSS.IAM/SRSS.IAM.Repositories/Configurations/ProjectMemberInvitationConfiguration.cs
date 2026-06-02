using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SRSS.IAM.Repositories.Entities;

namespace SRSS.IAM.Repositories.Configurations
{
       public class ProjectMemberInvitationConfiguration : IEntityTypeConfiguration<ProjectMemberInvitation>
       {
              public void Configure(EntityTypeBuilder<ProjectMemberInvitation> builder)
              {
                     builder.ToTable("project_member_invitations");

                     builder.HasKey(x => x.Id);

                     builder.Property(x => x.Id)
                            .HasColumnName("id");

                     builder.Property(x => x.ProjectId)
                            .HasColumnName("project_id")
                            .IsRequired();

                     builder.Property(x => x.InvitedUserId)
                            .HasColumnName("invited_user_id")
                            .IsRequired();

                     builder.Property(x => x.InvitedByUserId)
                            .HasColumnName("invited_by_user_id")
                            .IsRequired();


                     builder.Property(x => x.Status)
                            .HasColumnName("status")
                            .HasConversion<int>()
                            .IsRequired();

                     builder.Property(x => x.Role)
                            .HasColumnName("role")
                            .HasConversion<int>()
                            .IsRequired();

                     builder.Property(x => x.ResponseMessage)
                            .HasColumnName("response_message")
                            .HasMaxLength(500);

                     builder.Property(x => x.ExpiredAt)
                            .HasColumnName("expired_at");

                     builder.Property(x => x.RespondedAt)
                            .HasColumnName("responded_at");

                     builder.Property(x => x.CreatedAt)
                            .HasColumnName("created_at")
                            .HasDefaultValueSql("timezone('utc', now())")
                            .IsRequired();

                     // Relationships
                     builder.HasOne(x => x.Project)
                            .WithMany(p => p.ProjectMemberInvitations)
                            .HasForeignKey(x => x.ProjectId)
                            .OnDelete(DeleteBehavior.Cascade);

                     builder.HasOne(x => x.InvitedUser)
                            .WithMany()
                            .HasForeignKey(x => x.InvitedUserId)
                            .OnDelete(DeleteBehavior.Cascade);

                     builder.HasOne(x => x.InvitedByUser)
                            .WithMany()
                            .HasForeignKey(x => x.InvitedByUserId)
                            .OnDelete(DeleteBehavior.Cascade);

                     // Indexes
                     builder.HasIndex(x => x.ProjectId);
                     builder.HasIndex(x => x.InvitedUserId);
                     builder.HasIndex(x => x.Status);
                     builder.HasIndex(x => new { x.ProjectId, x.InvitedUserId, x.Status });
              }
       }
}
