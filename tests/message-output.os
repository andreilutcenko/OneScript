﻿
Перем юТест;

////////////////////////////////////////////////////////////////////
// Программный интерфейс

Функция ПолучитьСписокТестов(ЮнитТестирование) Экспорт
	
	юТест = ЮнитТестирование;
	
	ВсеТесты = Новый Массив;
	
	ВсеТесты.Добавить("ТестДолжен_ПроверитьЧтоСообщитьПринимаетВторойАргумент");
	
	Возврат ВсеТесты;
	
КонецФункции

Процедура ТестДолжен_ПроверитьЧтоСообщитьПринимаетВторойАргумент() Экспорт

	Сообщить("---------------------------------------------------------------------------");
	Сообщить("    Проверка корректной передачи второго параметра процедуре 'Сообщить'    ");
	Сообщить("---------------------------------------------------------------------------");
	Сообщить("Обычное");
	Сообщить("БезСтатуса", СтатусСообщения.БезСтатуса);
	Сообщить("Обычное", СтатусСообщения.Обычное);
	Сообщить("Информация", СтатусСообщения.Информация);
	Сообщить("Внимание", СтатусСообщения.Внимание);
	Сообщить("Важное", СтатусСообщения.Важное);
	Сообщить("ОченьВажное", СтатусСообщения.ОченьВажное);
	
КонецПроцедуры