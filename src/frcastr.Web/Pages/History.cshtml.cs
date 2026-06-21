using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace frcastr.Web.Pages;

[AllowAnonymous]
public class HistoryModel : PageModel
{
    public void OnGet() { }
}
