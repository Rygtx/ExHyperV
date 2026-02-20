import os
import re
import csv
import hashlib

# --- 配置 ---
SOURCE_DIR = r'./'
OUTPUT_CSV = 'all_pending_translation.csv'

# 正则表达式
CHINESE_REGEX = re.compile(r'[\u4e00-\u9fa5]+')
CS_STRING_REGEX = re.compile(r'\$?"([^"\\]*(?:\\.[^"\\]*)*)"')
XAML_ATTR_REGEX = re.compile(r'\b(Text|Content|Header|Title|ToolTip|Label|Value)="([^"]*[\u4e00-\u9fa5]+[^"]*)"')

# 过滤调试信息
DEBUG_KEYWORDS = ["Debug.", "Console.", "Log.", "Wmi", "ProcessAffinity", ">>>", "---"]

def should_skip(content, line_code):
    if any(k in line_code for k in DEBUG_KEYWORDS): return True
    if content.strip().upper().startswith("SELECT "): return True
    if line_code.strip().startswith("//"): return True
    return False

def extract_all():
    results = []
    seen = set()

    for root, dirs, files in os.walk(SOURCE_DIR):
        if any(d in root for d in ['bin', 'obj', '.git', '.vs']): continue
        
        for file in files:
            path = os.path.join(root, file)
            is_xaml = file.endswith('.xaml')
            is_cs = file.endswith('.cs')
            
            if not (is_xaml or is_cs): continue
            
            with open(path, 'r', encoding='utf-8', errors='ignore') as f:
                lines = f.readlines()
                for i, line in enumerate(lines):
                    # --- XAML 处理 ---
                    if is_xaml:
                        for m in XAML_ATTR_REGEX.finditer(line):
                            attr, content = m.groups()
                            if content not in seen:
                                results.append({'Key': f'Xaml_{hashlib.md5(content.encode()).hexdigest()[:6]}', 'Type': 'XAML', 'Content': content, 'Args': '', 'File': path, 'Line': i+1})
                                seen.add(content)
                    
                    # --- CS 处理 ---
                    elif is_cs:
                        for m in CS_STRING_REGEX.finditer(line):
                            raw_str = m.group(0)
                            content = m.group(1)
                            if CHINESE_REGEX.search(content) and not should_skip(content, line):
                                # 核心逻辑：提取变量并分离格式化指令
                                # 例如：{Value:0.#} -> Content: {0:0.#}, Args: Value
                                vars_in_str = re.findall(r'\{([^{}]+)\}', content)
                                formatted_content = content
                                args_list = []
                                for idx, v in enumerate(vars_in_str):
                                    if ':' in v:
                                        var_name, format_str = v.split(':', 1)
                                        formatted_content = formatted_content.replace(f'{{{v}}}', f'{{{idx}:{format_str}}}')
                                        args_list.append(var_name)
                                    else:
                                        formatted_content = formatted_content.replace(f'{{{v}}}', f'{{{idx}}}')
                                        args_list.append(v)
                                
                                if formatted_content not in seen:
                                    results.append({'Key': f'CS_{hashlib.md5(formatted_content.encode()).hexdigest()[:6]}', 'Type': 'CS', 'Content': formatted_content, 'Args': ",".join(args_list), 'File': path, 'Line': i+1})
                                    seen.add(formatted_content)
    return results

data = extract_all()
with open(OUTPUT_CSV, 'w', encoding='utf-8-sig', newline='') as f:
    writer = csv.DictWriter(f, fieldnames=['Key', 'Type', 'Content', 'Args', 'File', 'Line'])
    writer.writeheader()
    writer.writerows(data)
print(f"提取完成！共发现 {len(data)} 条中文（包含 XAML 和 CS）。文件已保存至: {OUTPUT_CSV}")