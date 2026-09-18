// WebGL 插件：把文本内容作为文件下载到浏览器
// 用于"一键导出验收报告"，评审现场可直接演示从系统输出到文件产物
mergeInto(LibraryManager.library, {

  DownloadTextFile: function (filenamePtr, contentPtr) {
    var filename = UTF8ToString(filenamePtr);
    var content = UTF8ToString(contentPtr);
    var blob = new Blob([content], { type: 'text/plain;charset=utf-8' });
    var url = URL.createObjectURL(blob);
    var link = document.createElement('a');
    link.href = url;
    link.download = filename;
    document.body.appendChild(link);
    link.click();
    document.body.removeChild(link);
    URL.revokeObjectURL(url);
  }

});
