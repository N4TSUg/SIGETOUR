import codecs

with codecs.open('D:/UNI/CAPSTONE/SIGETOUR/SIGETOUR.API/Views/Home/Index.cshtml', 'r', 'utf-8', errors='ignore') as f:
    text = f.read()

model_section = '''
<!-- Catálogo de Tours Principales -->
<section class="py-14 max-w-7xl mx-auto px-4 sm:px-6 lg:px-8">
    <div class="flex items-center gap-2 mb-8">
        <span class="w-2.5 h-2.5 rounded-full bg-primary"></span>
        <h2 class="text-2xl sm:text-3xl font-extrabold text-secondary mt-1">Explora Nuestro Catálogo</h2>
    </div>
    <div class="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-4 gap-6">
        @if (Model != null && Model.Any())
        {
            foreach (var tour in Model)
            {
                <div class="bg-white rounded-2xl overflow-hidden shadow-sm hover:shadow-xl transition-all duration-300 border border-outline-variant flex flex-col group">
                    <div class="relative h-48 overflow-hidden bg-surface-container">
                        @if (tour.Images != null && tour.Images.Any())
                        {
                            <img class="w-full h-full object-cover group-hover:scale-105 transition-transform duration-500" src="@tour.Images.First().ImageUrl" alt="@tour.Title">
                        }
                        else
                        {
                            <div class="w-full h-full flex flex-col items-center justify-center text-on-surface-variant/60">
                                <span class="material-symbols-outlined text-4xl mb-2">landscape</span>
                                <span class="text-sm font-semibold">Sin imágenes</span>
                            </div>
                        }
                        <span class="absolute top-3 right-3 bg-white/95 text-secondary font-bold text-xs px-2.5 py-1 rounded-full shadow-xs">
                            @tour.Duration
                        </span>
                        <span class="absolute top-3 left-3 bg-secondary text-white text-xs font-semibold px-2.5 py-1 rounded-full flex items-center gap-1 shadow-xs">
                            <span class="material-symbols-outlined text-[14px] text-amber-400" data-icon="star">star</span> 4.9
                        </span>
                    </div>
                    <div class="p-5 flex flex-col flex-grow">
                        <h3 class="font-bold text-lg text-secondary">@tour.Title</h3>
                        <p class="text-xs text-on-surface-variant mt-2 flex-grow leading-relaxed line-clamp-2">
                            @tour.Description
                        </p>
                        <div class="mt-4 pt-3 border-t border-outline-variant space-y-1.5 text-xs text-on-surface-variant">
                            <div class="flex items-center gap-2">
                                <span class="material-symbols-outlined text-[16px] text-primary" data-icon="explore">explore</span>
                                Categoría: @tour.Category
                            </div>
                        </div>
                        <div class="mt-5 flex items-center justify-between">
                            <div>
                                <span class="text-[11px] text-on-surface-variant block">Desde</span>
                                <span class="text-xl font-extrabold text-secondary">S/ @tour.BasePrice</span>
                            </div>
                            <a asp-controller="Tour" asp-action="Details" asp-route-slug="@tour.Slug" class="bg-primary hover:bg-primary-dark text-white px-4 py-2 rounded-lg font-bold text-xs transition-colors">
                                Ver Detalles
                            </a>
                        </div>
                    </div>
                </div>
            }
        }
    </div>
</section>
'''

idx = text.find('<!-- Featured Tour Packages')
if idx != -1:
    new_text = text[:idx] + model_section + text[idx:]
    with codecs.open('D:/UNI/CAPSTONE/SIGETOUR/SIGETOUR.API/Views/Home/Index.cshtml', 'w', 'utf-8-sig') as f:
        f.write(new_text)
    print('Injected successfully before Featured Packages')
else:
    print('Could not find Featured')
