using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace silaa.UI;

/// <summary>
/// Renders validated chart JSON as isolated, self-contained HTML/SVG. The WebView
/// receives no arbitrary model-authored markup and the page CSP blocks all network access.
/// </summary>
internal sealed class HtmlChartView : Border
{
    public HtmlChartView(string html, string source, string summary, string kind)
    {
        Background = Palette.Panel;
        BorderBrush = Palette.Divider;
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(10);
        ClipToBounds = true;

        var label = new TextBlock
        {
            Text = $"CHART · {kind.ToUpperInvariant()} · HTML",
            FontFamily = Palette.Mono,
            FontSize = 9.5,
            FontWeight = FontWeight.SemiBold,
            Foreground = Palette.Muted,
            VerticalAlignment = VerticalAlignment.Center
        };
        var copy = Palette.SmallButton("데이터 복사");
        copy.Click += async (_, _) => await Palette.CopyAsync(copy, source);
        var header = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(12, 6, 7, 5)
        };
        header.Children.Add(label);
        Grid.SetColumn(copy, 1);
        header.Children.Add(copy);

        var webView = new NativeWebView
        {
            Height = 360,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        AutomationProperties.SetName(webView, "HTML 차트");
        AutomationProperties.SetHelpText(webView, summary);
        webView.AttachedToVisualTree += (_, _) =>
            Dispatcher.UIThread.Post(() => webView.NavigateToString(html), DispatcherPriority.Loaded);

        var content = new Grid { RowDefinitions = new RowDefinitions("Auto,1,*") };
        content.Children.Add(header);
        var divider = new Border { Background = Palette.Divider };
        Grid.SetRow(divider, 1);
        content.Children.Add(divider);
        Grid.SetRow(webView, 2);
        content.Children.Add(webView);
        Child = content;
    }
}

internal static class ChartHtmlRenderer
{
    private const int MaximumSourceCharacters = 128_000;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    internal static bool TryBuild(string source, out string html, out string summary, out string kind, out string error)
    {
        html = "";
        summary = "";
        kind = "";
        error = "";
        try
        {
            if (source.Length > MaximumSourceCharacters)
                throw new ArgumentException("차트 데이터가 너무 큽니다.");
            var spec = JsonSerializer.Deserialize<ChartSpec>(source, JsonOptions)
                       ?? throw new ArgumentException("차트 JSON이 비어 있습니다.");
            Validate(spec);
            spec.Type = spec.Type.Trim().ToLowerInvariant() switch
            {
                "pie" => "donut",
                var value => value
            };
            spec.Title = string.IsNullOrWhiteSpace(spec.Title) ? "차트" : spec.Title.Trim();
            spec.Unit = spec.Unit?.Trim() ?? "";
            spec.Description = spec.Description?.Trim() ?? "";

            kind = spec.Type;
            summary = BuildSummary(spec);
            html = BuildHtml(spec);
            return true;
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidOperationException)
        {
            error = exception is JsonException ? "차트 JSON 형식을 확인하세요." : exception.Message;
            return false;
        }
    }

    private static void Validate(ChartSpec spec)
    {
        var type = spec.Type?.Trim().ToLowerInvariant();
        if (type is not ("bar" or "line" or "donut" or "pie"))
            throw new ArgumentException("차트 type은 bar, line, donut 중 하나여야 합니다.");
        if (spec.Title?.Length > 120 || spec.Description?.Length > 240 || spec.Unit?.Length > 12)
            throw new ArgumentException("차트 제목, 설명 또는 단위가 너무 깁니다.");
        if (spec.Labels is null || spec.Labels.Length is < 1 or > 24)
            throw new ArgumentException("차트 labels는 1~24개여야 합니다.");
        if (spec.Labels.Any(label => string.IsNullOrWhiteSpace(label) || label.Length > 40))
            throw new ArgumentException("차트 label은 1~40자의 문자열이어야 합니다.");
        if (spec.Series is null || spec.Series.Length is < 1 or > 6)
            throw new ArgumentException("차트 series는 1~6개여야 합니다.");
        foreach (var series in spec.Series)
        {
            if (string.IsNullOrWhiteSpace(series.Name) || series.Name.Length > 60)
                throw new ArgumentException("각 series에는 1~60자의 name이 필요합니다.");
            if (series.Values is null || series.Values.Length != spec.Labels.Length)
                throw new ArgumentException("각 series의 values 수는 labels 수와 같아야 합니다.");
            if (series.Values.Any(value => !double.IsFinite(value) || Math.Abs(value) > 1e15))
                throw new ArgumentException("차트 값은 유한한 숫자여야 합니다.");
        }
        if (type is "donut" or "pie")
        {
            if (spec.Series.Length != 1 || spec.Series[0].Values.Any(value => value < 0) || spec.Series[0].Values.Sum() <= 0)
                throw new ArgumentException("도넛 차트는 합계가 0보다 큰 하나의 음이 아닌 series를 사용해야 합니다.");
        }
    }

    private static string BuildSummary(ChartSpec spec)
    {
        var builder = new StringBuilder($"{spec.Title}. {spec.Type} 차트. ");
        foreach (var series in spec.Series)
        {
            builder.Append(series.Name).Append(": ");
            builder.AppendJoin(", ", spec.Labels.Zip(series.Values,
                (label, value) => $"{label} {value:0.##}{spec.Unit}"));
            builder.Append(". ");
        }
        return builder.ToString();
    }

    private static string BuildHtml(ChartSpec spec)
    {
        var data = JsonSerializer.Serialize(spec, JsonOptions);
        var dark = Palette.IsDark;
        var background = dark ? "#111111" : "#FCFCFA";
        var foreground = dark ? "#F2F2EF" : "#111111";
        var muted = dark ? "#9A9A94" : "#6B6B66";
        var divider = dark ? "#2B2B2B" : "#DCDCD6";
        var colors = dark
            ? "['#F2F2EF','#B9B9B2','#85857E','#5B5B56','#3D3D39','#72726C']"
            : "['#171717','#4C4C48','#777770','#9B9B94','#BEBEB6','#5E5E58']";

        return $$"""
<!doctype html>
<html lang="ko">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<meta http-equiv="Content-Security-Policy" content="default-src 'none'; style-src 'unsafe-inline'; script-src 'unsafe-inline'; img-src data:">
<style>
  *{box-sizing:border-box} html,body{margin:0;height:100%;overflow:hidden;background:{{background}};color:{{foreground}};font-family:'Segoe UI Variable Text','Segoe UI','Malgun Gothic',sans-serif}
  main{height:100%;padding:18px 20px 14px;display:flex;flex-direction:column;gap:8px}
  h2{font-size:16px;line-height:1.25;margin:0;font-weight:650} p{font-size:11px;line-height:1.4;margin:0;color:{{muted}};min-height:15px}
  #legend{min-height:20px;display:flex;gap:13px;align-items:center;flex-wrap:wrap;font-size:10.5px;color:{{muted}}}
  .legend-item{display:inline-flex;align-items:center;gap:5px}.swatch{width:8px;height:8px;border-radius:2px;display:inline-block}
  svg{display:block;width:100%;height:100%;min-height:0;overflow:visible} text{font-family:inherit;fill:{{muted}};font-size:10px}.grid{stroke:{{divider}};stroke-width:1}.axis{stroke:{{divider}};stroke-width:1}
  .sr-only{position:absolute!important;width:1px;height:1px;padding:0;margin:-1px;overflow:hidden;clip:rect(0,0,0,0);white-space:nowrap;border:0}
</style>
</head>
<body>
<main role="img" id="chart" aria-label="">
  <h2 id="title"></h2><p id="description"></p><div id="legend"></div>
  <svg id="plot" viewBox="0 0 760 270" role="presentation"></svg>
  <table id="data-table" class="sr-only"><caption></caption><thead><tr></tr></thead><tbody></tbody></table>
</main>
<script>
(() => {
  'use strict';
  const spec={{data}};
  const colors={{colors}};
  const NS='http://www.w3.org/2000/svg';
  const svg=document.getElementById('plot');
  const el=(name,attrs={},text='')=>{const node=document.createElementNS(NS,name);for(const [key,value] of Object.entries(attrs))node.setAttribute(key,String(value));if(text)node.textContent=text;return node};
  const format=value=>new Intl.NumberFormat('ko-KR',{maximumFractionDigits:2}).format(value)+(spec.unit||'');
  document.getElementById('title').textContent=spec.title;
  document.getElementById('description').textContent=spec.description||' ';
  document.getElementById('chart').setAttribute('aria-label',spec.title+' '+spec.type+' chart');
  const legend=document.getElementById('legend');
  const addLegend=(name,color)=>{const item=document.createElement('span');item.className='legend-item';const swatch=document.createElement('i');swatch.className='swatch';swatch.style.background=color;const text=document.createElement('span');text.textContent=name;item.append(swatch,text);legend.append(item)};
  const addTitle=(node,text)=>{const title=el('title',{},text);node.append(title);return node};
  const table=document.getElementById('data-table');table.querySelector('caption').textContent=spec.title;const header=table.querySelector('thead tr');
  ['구분',...spec.series.map(s=>s.name)].forEach(value=>{const th=document.createElement('th');th.textContent=value;header.append(th)});
  spec.labels.forEach((label,index)=>{const tr=document.createElement('tr');[label,...spec.series.map(s=>format(s.values[index]))].forEach(value=>{const td=document.createElement('td');td.textContent=value;tr.append(td)});table.querySelector('tbody').append(tr)});

  if(spec.type==='donut'){
    const values=spec.series[0].values,total=values.reduce((a,b)=>a+b,0),cx=380,cy=132,r=86,inner=52;
    let angle=-Math.PI/2;
    values.forEach((value,index)=>{const next=angle+(value/total)*Math.PI*2;const point=(radius,a)=>[cx+Math.cos(a)*radius,cy+Math.sin(a)*radius];const [x1,y1]=point(r,angle),[x2,y2]=point(r,next),[ix2,iy2]=point(inner,next),[ix1,iy1]=point(inner,angle);const large=next-angle>Math.PI?1:0;const path=el('path',{d:`M ${x1} ${y1} A ${r} ${r} 0 ${large} 1 ${x2} ${y2} L ${ix2} ${iy2} A ${inner} ${inner} 0 ${large} 0 ${ix1} ${iy1} Z`,fill:colors[index%colors.length]});svg.append(addTitle(path,`${spec.labels[index]}: ${format(value)}`));addLegend(`${spec.labels[index]} · ${format(value)}`,colors[index%colors.length]);angle=next});
    svg.append(el('text',{x:cx,y:cy-3,'text-anchor':'middle',style:'font-size:20px;font-weight:650;fill:{{foreground}}'},format(total)));
    svg.append(el('text',{x:cx,y:cy+17,'text-anchor':'middle'},'합계'));
    return;
  }

  spec.series.forEach((series,index)=>addLegend(series.name,colors[index%colors.length]));
  const left=58,right=18,top=12,bottom=39,width=760-left-right,height=270-top-bottom;
  const all=spec.series.flatMap(s=>s.values),min=Math.min(0,...all),max=Math.max(0,...all);const span=max-min||1;
  const y=value=>top+height-(value-min)/span*height;
  for(let tick=0;tick<=4;tick++){const value=min+span*tick/4,py=y(value);svg.append(el('line',{x1:left,y1:py,x2:left+width,y2:py,class:'grid'}));svg.append(el('text',{x:left-8,y:py+3,'text-anchor':'end'},format(value)))}
  svg.append(el('line',{x1:left,y1:top,x2:left,y2:top+height,class:'axis'}));
  const labelStep=Math.max(1,Math.ceil(spec.labels.length/8));
  spec.labels.forEach((label,index)=>{if(index%labelStep&&index!==spec.labels.length-1)return;const x=left+(index+.5)*width/spec.labels.length;svg.append(el('text',{x,y:top+height+23,'text-anchor':'middle'},label.length>12?label.slice(0,11)+'…':label))});

  if(spec.type==='bar'){
    const group=width/spec.labels.length,bar=Math.min(38,group*.72/spec.series.length),zero=y(0);
    spec.series.forEach((series,seriesIndex)=>series.values.forEach((value,index)=>{const x=left+index*group+(group-bar*spec.series.length)/2+seriesIndex*bar;const py=y(value);const rect=el('rect',{x,y:Math.min(py,zero),width:Math.max(1,bar-2),height:Math.max(1,Math.abs(zero-py)),rx:2,fill:colors[seriesIndex%colors.length]});svg.append(addTitle(rect,`${spec.labels[index]} · ${series.name}: ${format(value)}`))}));
  }else{
    spec.series.forEach((series,seriesIndex)=>{const points=series.values.map((value,index)=>[left+(spec.labels.length === 1 ? .5 : index/(spec.labels.length-1))*width,y(value)]);svg.append(el('polyline',{points:points.map(p=>p.join(',')).join(' '),fill:'none',stroke:colors[seriesIndex%colors.length],'stroke-width':2.5,'stroke-linejoin':'round','stroke-linecap':'round'}));points.forEach((point,index)=>{const dot=el('circle',{cx:point[0],cy:point[1],r:3.5,fill:colors[seriesIndex%colors.length],stroke:'{{background}}','stroke-width':2});svg.append(addTitle(dot,`${spec.labels[index]} · ${series.name}: ${format(series.values[index])}`))})});
  }
})();
</script>
</body>
</html>
""";
    }

    private sealed class ChartSpec
    {
        public string Type { get; set; } = "";
        public string Title { get; set; } = "";
        public string Description { get; set; } = "";
        public string Unit { get; set; } = "";
        public string[] Labels { get; set; } = [];
        public ChartSeries[] Series { get; set; } = [];
    }

    private sealed class ChartSeries
    {
        public string Name { get; set; } = "";
        public double[] Values { get; set; } = [];
    }
}
