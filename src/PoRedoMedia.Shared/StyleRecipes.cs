namespace PoRedoMedia.Shared;

public sealed record StyleRecipe(
    string Id,
    string Title,
    string Category,
    string Description,
    string PromptSnippet,
    string[] Tags);

public static class StyleRecipeCatalog
{
    public static readonly IReadOnlyList<string> Categories =
    [
        "All",
        "Fine Art",
        "Pop & Retro",
        "Craft & 3D",
        "Cinematic",
        "Satire & Parody"
    ];

    public static readonly IReadOnlyList<StyleRecipe> All =
    [
        // ── Fine Art ──────────────────────────────────────────────────────────
        new("caravaggio", "Baroque Chiaroscuro", "Fine Art",
            "Violent contrasts of light and darkness, theatrical emotional gravity, and rich oil craquelure.",
            "Colossal 17th-century Baroque oil painting in the style of Caravaggio. Dramatic chiaroscuro with a single harsh shaft of divine illumination carving out deep shadows, heavy varnish, authentic canvas craquelure and classical museum framing.",
            ["Oil Painting", "Chiaroscuro", "17th Century", "Dramatic"]),

        new("vangogh", "Post-Impressionist Impasto", "Fine Art",
            "Swirling dynamic brushstrokes, thick rhythmic paint impasto, and glowing celestial colors.",
            "Vibrant Post-Impressionist masterpiece in the style of Vincent van Gogh. Thick, rhythmic impasto knife strokes, swirling celestial skies with luminous halos of cobalt blue, golden ochre, and emerald green, textured raw linen canvas visible underneath.",
            ["Impasto", "Van Gogh", "Swirls", "Expressive"]),

        new("hokusai", "Edo Woodblock Print", "Fine Art",
            "Ukiyo-e Japanese woodblock print with flat color planes, crisp ink lines, and washi paper grain.",
            "Traditional Japanese Edo-period Ukiyo-e woodblock print in the style of Hokusai and Hiroshige. Precise black sumi ink outlines, graded bokashi watercolor washes in Prussian blue and vermillion, printed on handmade fibrous washi mulberry paper with authentic woodgrain embossing.",
            ["Ukiyo-e", "Woodblock", "Hokusai", "Japanese"]),

        new("monet", "Giverny Impressionism", "Fine Art",
            "Dappled outdoor light, broken color brushwork, and soft atmosphere.",
            "Plein-air French Impressionist oil painting in the style of Claude Monet. Dappled natural sunlight, loose visible brushwork capturing fleeting atmospheric haze, soft pastel reflections, luminous water lilies and lush garden foliage.",
            ["Impressionism", "Pastel", "Atmospheric", "Soft"]),

        // ── Pop & Retro ───────────────────────────────────────────────────────
        new("anime90s", "90s Cel Shaded Anime", "Pop & Retro",
            "Classic hand-painted cel animation with film grain and retro neon palettes.",
            "Vintage 1990s retro anime still, hand-painted acrylic animation cel aesthetic. Rich gouache painted backgrounds, clean ink linework, subtle chromatic aberration, soft CRT television scanline glow, and a nostalgic analog VHS color cast.",
            ["Anime", "90s", "Cel Shading", "Retro"]),

        new("synthwave", "Synthwave Cyberpunk", "Pop & Retro",
            "Electric cyan and magenta neon, wireframe horizons, and retro-futuristic cityscapes.",
            "Outrun synthwave retro-futuristic aesthetic. Glowing electric magenta and cyan neon lights reflecting off wet asphalt, 1980s chrome highlights, wireframe grid horizon beneath a hazy purple sunset, volumetric fog, and lens flares.",
            ["Cyberpunk", "Synthwave", "Neon", "80s"]),

        new("polaroid70s", "1970s Vintage Polaroid", "Pop & Retro",
            "Warm faded tones, authentic light leaks, and instant film border.",
            "Authentic 1970s Polaroid Land camera instant photograph. Warm amber color cast, soft contrast, mild light leak along the edge, gentle vignette, slight focal softness, and a classic textured white square border.",
            ["Polaroid", "Vintage", "Warm", "Light Leak"]),

        new("rubberhose", "1930s Rubber Hose Toon", "Pop & Retro",
            "Monochrome vintage cartoon with noodle limbs and hand-drawn celluloid grit.",
            "1930s vintage Fleischer-style rubber hose animation still. Black and white monochrome palette, pie-cut eyes, bouncy noodle limbs, grainy 35mm film scratches, projector dust, and whimsical hand-drawn ink character styling.",
            ["Cartoon", "1930s", "Rubber Hose", "Monochrome"]),

        // ── Craft & 3D ────────────────────────────────────────────────────────
        new("claymation", "Stop-Motion Claymation", "Craft & 3D",
            "Plasticine clay textures, visible thumbprints, and tactile miniature lighting.",
            "Handmade tactile stop-motion clay animation still in the style of Aardman. Sculpted plasticine clay with visible thumbprint textures, miniature studio directional lighting, macro lens depth of field, felt and cardboard miniature set pieces.",
            ["Claymation", "Stop Motion", "Tactile", "Miniature"]),

        new("papercraft", "Paper Cutout Shadowbox", "Craft & 3D",
            "Layered cardstock silhouettes with dimensional depth and soft cast shadows.",
            "Intricate multi-layered papercut shadowbox art. Dimensional layers of laser-cut heavy textured cardstock, back-lit with warm ambient LEDs casting soft shadows between sheets, crisp paper edges, and rich geometric depth.",
            ["Papercraft", "Shadowbox", "Layers", "Craft"]),

        new("lowpoly", "Low-Poly Diorama", "Craft & 3D",
            "Faceted geometric 3D polygons, soft isometric lighting, and clean gradients.",
            "Charming isometric low-poly 3D diorama rendered in Octane. Clean faceted polygon geometry, vibrant matte pastel materials, soft ambient occlusion, miniature tilt-shift perspective, and smooth gradient lighting.",
            ["Low Poly", "3D", "Isometric", "Octane"]),

        new("stainedglass", "Gothic Stained Glass", "Craft & 3D",
            "Jewel-toned translucent glass, soldered lead cames, and radiant backlighting.",
            "Majestic 14th-century cathedral stained glass window. Thick dark lead cames framing translucent jewel-toned cobalt, ruby, and amber glass panels, sunlight streaming through projecting colorful radiant caustic patterns.",
            ["Stained Glass", "Gothic", "Jewel Tones", "Cathedral"]),

        // ── Cinematic ─────────────────────────────────────────────────────────
        new("cinematic35mm", "Cinematic 35mm Film Still", "Cinematic",
            "Prestige cinema look with shallow depth of field, anamorphic bokeh, and Kodak tone.",
            "Cinematic film still shot on 35mm Kodak Vision3 stock with an anamorphic lens. Cinematic widescreen aspect ratio, organic fine grain, authentic horizontal blue streak flares, rich organic skin tones, and moody atmospheric lighting.",
            ["Cinematic", "35mm", "Anamorphic", "Kodak"]),

        new("unreal5", "Hyperreal Octane Render", "Cinematic",
            "Subsurface scattering, Ray-traced global illumination, and photorealistic detail.",
            "Hyperrealistic 8k digital render powered by Unreal Engine 5 Lumen and Octane. Photorealistic micro-surface roughness, subsurface scattering on skin, realistic optical depth, volumetrics, and pristine physical lighting.",
            ["Unreal Engine", "Octane", "8K", "Photoreal"]),

        new("editorial", "Vogue Editorial Studio", "Cinematic",
            "High-fashion magazine cover lighting, dramatic wardrobe, and crisp studio backdrop.",
            "High-fashion editorial portrait shot for a luxury magazine cover. Clean high-contrast beauty dish studio lighting, sculptural wardrobe styling, crisp focus, muted sophisticated color grading, and razor-sharp textures.",
            ["Fashion", "Editorial", "Studio", "High Contrast"]),

        // ── Satire & Parody ───────────────────────────────────────────────────
        new("marginalia", "Medieval Marginalia Doodle", "Satire & Parody",
            "Cracked parchment, bizarre mythical beasts, and comically awkward monk illustrations.",
            "Illuminated medieval manuscript margin on stained parchment with iron gall ink and gold leaf. Gloriously awkward anatomical proportions of a 13th-century monk drawing, accompanied by armored snails and a lute-playing rabbit with dense blackletter script.",
            ["Medieval", "Manuscript", "Absurd", "Parchment"]),

        new("tabloid", "Vintage Tabloid Front Page", "Satire & Parody",
            "Sensationalist newsprint, half-tone dots, and screaming headline typography.",
            "Yellowing 1980s sensational supermarket tabloid front page. Grainy black-and-white halftone print dots with cheap color ink registration misalignment, screaming bold black headline banner, sensational red badge, and weathered folded paper creases.",
            ["Tabloid", "Halftone", "Newspaper", "Vintage"]),

        new("corkboard", "Detective Conspiracy Board", "Satire & Parody",
            "String-connected polaroids, frenetic red marker circles, and bulletin board paranoia.",
            "Cluttered evidence corkboard in a dimly lit investigative bunker. Taut red yarn crisscrossing between pinned polaroids, frantic marker scribbles, sticky notes, newspaper clippings, and a single overhead desk lamp casting stark shadows.",
            ["Conspiracy", "Corkboard", "Polaroid", "Mystery"]),

        new("wanted", "Wild West Wanted Poster", "Satire & Parody",
            "Letterpress woodcut typography, weathered parchment, and saloon post bill.",
            "1880s American frontier Wild West wanted handbill pinned to weathered cedar planks. Woodcut engraved portrait, sun-bleached coffee-stained paper with torn ragged edges, heavy slab-serif letterpress type, and an absurd bounty reward.",
            ["Wild West", "Wanted Poster", "Woodcut", "Letterpress"])
    ];
}
